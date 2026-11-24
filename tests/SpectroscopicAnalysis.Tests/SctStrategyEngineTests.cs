using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.SctStrategy;

namespace SpectroscopicAnalysis.Tests;

/// <summary>
/// SCT 策略引擎测试（引擎设计文档 §12）：优先级队列/去重合并/急停清队、
/// 触发判断（按数量与时间插入检查样、切自动模式/定时标准化、合金切换/期限类型标准化）、
/// 选样规则（LeastUsed/RoundRobin/合金匹配）、超差闭环（重测上限）、测高联动与厚度流转。
/// </summary>
public sealed class SctStrategyEngineTests
{
    /// <summary>可拨动的假时钟（避免引入新测试依赖包）。</summary>
    private sealed class SettableTimeProvider(DateTime start) : TimeProvider
    {
        private DateTimeOffset _now = new(start, TimeSpan.Zero);

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static SctSample MakeSample(
        string id,
        string type = SampleType.SettingUp,
        string status = SampleStatus.Normal,
        string? alloy = null,
        int usage = 0,
        int maxUsage = 50,
        double thickness = 10,
        double minThickness = 5,
        DateTime? lastUse = null,
        bool isActive = true) => new()
    {
        SampleId = id,
        SampleName = id,
        SampleType = type,
        AlloyType = alloy,
        Status = status,
        UsageCount = usage,
        MaxUsageCount = maxUsage,
        CurrentThickness = thickness,
        MinThickness = minThickness,
        LastUseTime = lastUse,
        IsActive = isActive,
    };

    private readonly FakeSctPolicyStore _policy = new();
    private readonly FakeSampleRepository _samples = new();
    private readonly FakeOperationLogRepository _logs = new();
    private readonly FakeErrorCollector _errors = new();
    private readonly SettableTimeProvider _time = new(new DateTime(2026, 11, 12, 8, 0, 0));

    private SctStrategyEngine Create() => new(_policy, _samples, _logs, _errors, _time);

    // ==================== 优先级队列 ====================

    [Fact]
    public void 队列_优先级次序与互斥()
    {
        var engine = Create();
        engine.SubmitTask(SctTaskType.HeightMeasure, "SUS-001", "测高");
        engine.SubmitTask(SctTaskType.Standardize, "SUS-002", "人工触发");
        engine.SubmitTask(SctTaskType.ControlCheck, "CTL-001", "按数量插入");

        // P29：标准化(1) > 控制检查(2) > 测高(4)
        Assert.True(engine.TryDequeue(out var t1));
        Assert.Equal(SctTaskType.Standardize, t1.TaskType);
        Assert.Equal(SctTaskStatus.Running, t1.Status);

        // 互斥：有 Running 任务时不出队（引擎设计 §6.2）
        Assert.False(engine.TryDequeue(out _));

        engine.CompleteTask(t1.TaskId, success: true, null);
        Assert.True(engine.TryDequeue(out var t2));
        Assert.Equal(SctTaskType.ControlCheck, t2.TaskType);

        engine.CompleteTask(t2.TaskId, true, null);
        Assert.True(engine.TryDequeue(out var t3));
        Assert.Equal(SctTaskType.HeightMeasure, t3.TaskType);

        engine.CompleteTask(t3.TaskId, true, null);
        Assert.False(engine.TryDequeue(out _));
    }

    [Fact]
    public void 队列_同优先级FIFO与失败不自动重试()
    {
        _samples.Store.Add(MakeSample("SUS-001"));
        _samples.Store.Add(MakeSample("SUS-002"));
        var engine = Create();

        engine.NotifyModeChanged(true);             // 切自动模式 → 标准化（选 SUS-001，LeastUsed 并列取首）
        _samples.Store.First(s => s.SampleId == "SUS-001").UsageCount = 99;   // 模拟已使用，下次选样轮换
        engine.NotifyModeChanged(false);
        engine.NotifyModeChanged(true);             // 再切一次 → 标准化（选 SUS-002，不同目标不去重）

        Assert.True(engine.TryDequeue(out var t1));
        engine.CompleteTask(t1.TaskId, true, null);
        Assert.True(engine.TryDequeue(out var t2));     // 互斥：先完成再出队下一个
        Assert.True(t1.EnqueueTime <= t2.EnqueueTime);  // FIFO
        Assert.Equal(SctTaskType.Standardize, t1.TaskType);
        Assert.Equal(SctTaskType.Standardize, t2.TaskType);

        engine.CompleteTask(t2.TaskId, success: false, "设备超时");
        Assert.Equal(SctTaskStatus.Failed, t2.Status);  // 失败不自动重试（引擎设计 §6.2）
        Assert.Contains(_errors.Collected, c => c.Message.Contains("执行失败"));
    }

    [Fact]
    public void 队列_手动置顶同档提前_多次置顶逆序()
    {
        var engine = Create();
        var id1 = engine.SubmitTask(SctTaskType.Production, "PL-001", "注册入队");
        var id2 = engine.SubmitTask(SctTaskType.Production, "PL-002", "注册入队");
        var id3 = engine.SubmitTask(SctTaskType.Production, "PL-003", "注册入队");

        // 初始 FIFO：PL-001 最先
        Assert.Equal(id1, engine.GetQueuedTasks().First().TaskId);

        // 置顶 PL-003 → 排到队首；再置顶 PL-002 → 两次点击逆序，PL-002 最新最前
        Assert.True(engine.MoveToTop(id3));
        Assert.Equal(id3, engine.GetQueuedTasks().First().TaskId);
        Assert.True(engine.MoveToTop(id2));
        var order = engine.GetQueuedTasks().Select(t => t.TaskId).ToList();
        Assert.Equal(new[] { id2, id3, id1 }, order);

        // 出队按同样次序取任务
        Assert.True(engine.TryDequeue(out var t));
        Assert.Equal(id2, t.TaskId);
    }

    [Fact]
    public void 队列_手动置顶不越过更高档位_不可调整非排队任务()
    {
        var engine = Create();
        var prodId = engine.SubmitTask(SctTaskType.Production, "PL-001", "注册入队");
        engine.SubmitTask(SctTaskType.ControlCheck, "CTL-001", "按数量插入");
        engine.SubmitTask(SctTaskType.Standardize, "SUS-001", "人工触发");

        Assert.True(engine.MoveToTop(prodId));

        // P29 不被破坏：标准化(1) > 控制检查(2) > 置顶后的生产任务(5)
        var queued = engine.GetQueuedTasks().Select(t => t.TaskType).ToList();
        Assert.Equal(new[] { SctTaskType.Standardize, SctTaskType.ControlCheck, SctTaskType.Production }, queued);

        // 逐个出队（互斥：先完成再出队下一个）让生产任务变为执行中，再尝试置顶 → 拒绝
        Assert.True(engine.TryDequeue(out var t1));
        engine.CompleteTask(t1.TaskId, true, null);
        Assert.True(engine.TryDequeue(out var t2));
        engine.CompleteTask(t2.TaskId, true, null);
        Assert.True(engine.TryDequeue(out var t3));
        Assert.Equal(SctTaskType.Production, t3.TaskType);
        Assert.False(engine.MoveToTop(t3.TaskId));      // 执行中不可调整

        // 不存在的任务号同样拒绝
        Assert.False(engine.MoveToTop(9999));
    }

    [Fact]
    public void 队列_重复目标同类型去重合并()
    {
        var engine = Create();
        _samples.Store.Add(MakeSample("SUS-001"));
        var id1 = engine.SubmitTask(SctTaskType.HeightMeasure, "SUS-001", "人工触发");
        var id2 = engine.SubmitTask(SctTaskType.HeightMeasure, "SUS-001", "人工触发");

        Assert.Equal(id1, id2);     // 去重合并返回同一 TaskId
        Assert.True(engine.TryDequeue(out _));
        Assert.False(engine.TryDequeue(out _));     // 队列中只有一个
    }

    [Fact]
    public void 急停_清空队列并冻结闭环()
    {
        var engine = Create();
        _samples.Store.Add(MakeSample("SUS-001"));
        engine.NotifyControlResult("CTL-001", passed: false);   // 激活闭环 → 标准化入队

        engine.EmergencyReset();

        Assert.False(engine.TryDequeue(out _));                 // 队列清空（Cancelled）
        // 闭环被冻结：标准化完成不再触发重测
        engine.NotifyStandardizeCompleted("SUS-001");
        Assert.False(engine.TryDequeue(out _));
    }

    // ==================== 触发判断 ====================

    [Fact]
    public void 触发_按数量插入检查样()
    {
        _policy.Current.ControlInsertMode = ControlInsertMode.ByCount;
        _policy.Current.ControlInsertCount = 3;
        _samples.Store.Add(MakeSample("CTL-001", SampleType.Control, alloy: "6061"));
        _samples.Store.Add(MakeSample("P-001", SampleType.Production));
        _samples.Store.Add(MakeSample("P-002", SampleType.Production));
        _samples.Store.Add(MakeSample("P-003", SampleType.Production));
        var engine = Create();

        engine.NotifyAlloySwitched("6061");     // 合金匹配用
        engine.NotifyAnalysisCompleted("P-001");
        engine.NotifyAnalysisCompleted("P-002");
        Assert.False(engine.TryDequeue(out _)); // 未达阈值不入队

        engine.NotifyAnalysisCompleted("P-003");
        Assert.True(engine.TryDequeue(out var task));
        Assert.Equal(SctTaskType.ControlCheck, task.TaskType);
        Assert.Equal("CTL-001", task.SampleId); // 轮换选样（唯一候选）
    }

    [Fact]
    public void 触发_按数量插入检查样_生产样不在SCT表同样计数()
    {
        // 回归：生产样档案在 Production_Sample（SCT 仓储查不到为 null），
        // 此前 null 提前 return 导致真实生产流程计数恒 0、检查样永不插入
        _policy.Current.ControlInsertMode = ControlInsertMode.ByCount;
        _policy.Current.ControlInsertCount = 2;
        _samples.Store.Add(MakeSample("CTL-001", SampleType.Control, alloy: "6061"));
        var engine = Create();
        engine.NotifyAlloySwitched("6061");

        engine.NotifyAnalysisCompleted("P-201");    // 未注册进 SCT 表的生产样
        Assert.False(engine.TryDequeue(out _));     // 未达阈值不入队

        engine.NotifyAnalysisCompleted("P-202");
        Assert.True(engine.TryDequeue(out var task));   // 达阈值插入检查样
        Assert.Equal(SctTaskType.ControlCheck, task.TaskType);
        Assert.Equal("CTL-001", task.SampleId);

        // 计数已归零：再次完成生产样不重复入队
        engine.CompleteTask(task.TaskId, true, null);
        engine.NotifyAnalysisCompleted("P-203");
        Assert.False(engine.TryDequeue(out _));
    }

    [Fact]
    public void 触发_按时间插入检查样与定时标准化()
    {
        _policy.Current.ControlInsertMode = ControlInsertMode.ByTime;
        _policy.Current.ControlInsertMinutes = 60;
        _policy.Current.PeriodicStandardizeEnabled = true;
        _policy.Current.PeriodicStandardizeHours = 8;
        _samples.Store.Add(MakeSample("CTL-001", SampleType.Control));
        _samples.Store.Add(MakeSample("SUS-001"));
        var engine = Create();

        engine.OnTick();                        // 上次时间未知 → 视为到期
        Assert.True(engine.TryDequeue(out var t1));
        Assert.Equal(SctTaskType.Standardize, t1.TaskType);     // 优先级 1 先出队
        engine.CompleteTask(t1.TaskId, true, null);
        engine.NotifyStandardizeCompleted("SUS-001");           // 标准化完成 → 记录标准化时间

        Assert.True(engine.TryDequeue(out var t2));
        Assert.Equal(SctTaskType.ControlCheck, t2.TaskType);
        engine.CompleteTask(t2.TaskId, true, null);
        engine.NotifyAnalysisCompleted("CTL-001");              // 控制检查完成 → 记录检查时间

        engine.OnTick();                                        // 均未到期不再触发
        Assert.False(engine.TryDequeue(out _));

        _time.Advance(TimeSpan.FromHours(8));
        engine.OnTick();
        Assert.True(engine.TryDequeue(out _));                  // 标准化到期重新触发
    }

    [Fact]
    public void 触发_切自动模式标准化仅边沿一次()
    {
        _samples.Store.Add(MakeSample("SUS-001"));
        var engine = Create();

        engine.NotifyModeChanged(true);
        engine.NotifyModeChanged(true);                         // 保持 Auto 不重复触发
        Assert.True(engine.TryDequeue(out var t1));
        engine.CompleteTask(t1.TaskId, true, null);
        Assert.False(engine.TryDequeue(out _));

        engine.NotifyModeChanged(false);
        engine.NotifyModeChanged(true);                         // Manual→Auto 再触发
        Assert.True(engine.TryDequeue(out _));
    }

    [Fact]
    public void 触发_合金切换类型标准化与无匹配报警()
    {
        _policy.Current.TypeStandardizeOnAlloySwitch = true;
        _samples.Store.Add(MakeSample("TS-001", SampleType.TypeStd, alloy: "6061"));
        var engine = Create();

        engine.NotifyAlloySwitched("3003");                     // 无匹配（精确匹配）
        Assert.False(engine.TryDequeue(out _));
        Assert.Contains(_errors.Collected, c => c.Code == ErrorCodes.SctTypeStdUnavailable);

        engine.NotifyAlloySwitched("6061");
        Assert.True(engine.TryDequeue(out var task));
        Assert.Equal(SctTaskType.TypeStandardize, task.TaskType);
        Assert.Equal("TS-001", task.SampleId);
    }

    // ==================== 选样规则 ====================

    [Fact]
    public void 选样_LeastUsed取使用次数最少()
    {
        _policy.Current.SettingSampleSelectionMode = SettingSampleSelectionMode.LeastUsed;
        _samples.Store.Add(MakeSample("SUS-A", usage: 40));
        _samples.Store.Add(MakeSample("SUS-B", usage: 5));
        var engine = Create();

        engine.NotifyModeChanged(true);
        Assert.True(engine.TryDequeue(out var task));
        Assert.Equal("SUS-B", task.SampleId);
    }

    [Fact]
    public void 选样_RoundRobin取上次使用最早()
    {
        _policy.Current.SettingSampleSelectionMode = SettingSampleSelectionMode.RoundRobin;
        _samples.Store.Add(MakeSample("SUS-A", usage: 1, lastUse: new DateTime(2026, 11, 10)));
        _samples.Store.Add(MakeSample("SUS-B", usage: 1, lastUse: new DateTime(2026, 11, 11)));
        var engine = Create();

        engine.NotifyModeChanged(true);
        Assert.True(engine.TryDequeue(out var task));
        Assert.Equal("SUS-A", task.SampleId);                   // 上次使用最早 → 轮询
    }

    [Fact]
    public void 选样_NeedMeasure与Disabled不参与且无可用报警()
    {
        _samples.Store.Add(MakeSample("SUS-A", status: SampleStatus.NeedMeasure));
        _samples.Store.Add(MakeSample("SUS-B", status: SampleStatus.Disabled));
        _samples.Store.Add(MakeSample("SUS-C", isActive: false));
        var engine = Create();

        engine.NotifyModeChanged(true);
        Assert.False(engine.TryDequeue(out _));
        Assert.Contains(_errors.Collected, c => c.Code == ErrorCodes.SctSettingSampleUnavailable);
    }

    [Fact]
    public void 选样_检查样轮换选最久未用且不做合金过滤()
    {
        _policy.Current.ControlInsertCount = 1;
        _samples.Store.Add(MakeSample("CTL-A", SampleType.Control));
        _samples.Store.Add(MakeSample("CTL-B", SampleType.Control));
        _samples.Store.Add(MakeSample("P-001", SampleType.Production, alloy: "3003"));
        _samples.Store[0].LastUseTime = DateTime.Parse("2026-11-08 08:00");   // CTL-A 最久未用
        _samples.Store[1].LastUseTime = DateTime.Parse("2026-11-09 08:00");
        var engine = Create();

        engine.NotifyAlloySwitched("3003");
        engine.NotifyAnalysisCompleted("P-001");
        Assert.True(engine.TryDequeue(out var task));
        Assert.Equal("CTL-A", task.SampleId);                   // 检查样不关联合金，仅按轮换（最久未用）选取
    }

    // ==================== 超差闭环 ====================

    [Fact]
    public void 闭环_超差标准化重测通过恢复()
    {
        _samples.Store.Add(MakeSample("SUS-001"));
        var engine = Create();                                  // AutoStandardizeOnControlFail=true、MaxRecheck=1

        engine.NotifyControlResult("CTL-001", passed: false);   // 首次超差 → 标准化
        Assert.True(engine.TryDequeue(out var std));
        Assert.Equal(SctTaskType.Standardize, std.TaskType);
        Assert.Equal("SUS-001", std.SampleId);                  // 选样可用（闭环前置）

        engine.CompleteTask(std.TaskId, true, null);
        engine.NotifyStandardizeCompleted("SUS-001");           // 标准化完成 → 重测验证
        Assert.True(engine.TryDequeue(out var recheck));
        Assert.Equal(SctTaskType.ControlCheck, recheck.TaskType);
        Assert.Equal("CTL-001", recheck.SampleId);
        Assert.True(recheck.IsRecheck);

        engine.CompleteTask(recheck.TaskId, true, null);
        engine.NotifyControlResult("CTL-001", passed: true);    // 通过 → 闭环结束
        Assert.False(engine.TryDequeue(out _));
        // 结果留痕已并入编排层完成日志行（一行一任务），引擎不再单独写日志
        Assert.DoesNotContain(_logs.Inserted, l => l.OperationType == "ControlCheck");
    }

    [Fact]
    public void 闭环_重测耗尽报警暂停()
    {
        _policy.Current.ControlRecheckMaxCount = 1;
        var engine = Create();
        _samples.Store.Add(MakeSample("SUS-001"));

        engine.NotifyControlResult("CTL-001", passed: false);   // 超差 → 标准化
        engine.TryDequeue(out var std);
        engine.CompleteTask(std.TaskId, true, null);
        engine.NotifyStandardizeCompleted("SUS-001");           // → 重测
        engine.TryDequeue(out var recheck);
        engine.CompleteTask(recheck.TaskId, true, null);
        engine.NotifyControlResult("CTL-001", passed: false);   // 重测仍超差 → 耗尽（Max=1）

        Assert.False(engine.TryDequeue(out _));                 // 不再入队标准化
        Assert.Contains(_errors.Collected, c => c.Code == ErrorCodes.SctRecheckExhausted);
    }

    [Fact]
    public void 闭环_未启用自动标准化直接报警()
    {
        _policy.Current.AutoStandardizeOnControlFail = false;
        var engine = Create();

        engine.NotifyControlResult("CTL-001", passed: false);
        Assert.False(engine.TryDequeue(out _));
        Assert.Contains(_errors.Collected, c => c.Code == ErrorCodes.SctRecheckExhausted);
    }

    // ==================== 测高联动与厚度流转 ====================

    [Fact]
    public void 测高联动_次数达阈值置NeedMeasure并插测高任务()
    {
        _samples.Store.Add(MakeSample("SUS-001", usage: 50, maxUsage: 50));
        var engine = Create();

        engine.NotifyAnalysisCompleted("SUS-001");              // 标样完成（Workflow 已落库次数）

        var sample = _samples.GetById("SUS-001")!;
        Assert.Equal(SampleStatus.NeedMeasure, sample.Status);  // 置 NeedMeasure
        Assert.True(engine.TryDequeue(out var task));
        Assert.Equal(SctTaskType.HeightMeasure, task.TaskType);
        Assert.Equal("SUS-001", task.SampleId);
        Assert.Equal(SctStrategyEngine.PriorityHeightMeasure, task.Priority);
    }

    [Fact]
    public void 测高联动_厚度充足恢复正常()
    {
        _samples.Store.Add(MakeSample("SUS-001", status: SampleStatus.NeedMeasure, thickness: 9, minThickness: 8));
        var engine = Create();

        engine.NotifyHeightMeasured("SUS-001", thickness: 8.5);

        var sample = _samples.GetById("SUS-001")!;
        Assert.Equal(SampleStatus.Normal, sample.Status);       // 清 NeedMeasure
        Assert.Equal(8.5, sample.CurrentThickness);
        Assert.NotNull(sample.LastHeightCheckTime);
        // 留痕已并入编排层完成日志行（一行一任务），引擎不再单独写日志
        Assert.DoesNotContain(_logs.Inserted, l => l.OperationType == "HeightMeasure");
    }

    [Fact]
    public void 测高联动_低于最小厚度禁用并报警()
    {
        _samples.Store.Add(MakeSample("SUS-001", status: SampleStatus.NeedMeasure, thickness: 9, minThickness: 8));
        var engine = Create();

        engine.NotifyHeightMeasured("SUS-001", thickness: 6);

        var sample = _samples.GetById("SUS-001")!;
        Assert.Equal(SampleStatus.Disabled, sample.Status);
        Assert.Contains(_errors.Collected, c => c.Code == ErrorCodes.SctThicknessBelowMinimum);
        // 留痕已并入编排层完成日志行（一行一任务），引擎不再单独写日志
        Assert.DoesNotContain(_logs.Inserted, l => l.OperationType == "HeightMeasure");
    }
}
