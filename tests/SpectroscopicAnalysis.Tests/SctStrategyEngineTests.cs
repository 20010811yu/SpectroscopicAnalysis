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
    public void 队列_删除排队任务_置已取消留痕且出队()
    {
        var engine = Create();
        var id1 = engine.SubmitTask(SctTaskType.Production, "PL-001", "注册入队");
        var id2 = engine.SubmitTask(SctTaskType.Production, "PL-002", "注册入队");

        // 删除排队任务：成功返回 true，任务出队并置 Cancelled
        Assert.True(engine.RemoveQueuedTask(id2));
        Assert.Equal(new[] { id1 }, engine.GetQueuedTasks().Select(t => t.TaskId).ToList());

        // 任务不凭空消失：历史留痕（Cancelled），状态机无自创状态名
        var history = engine.GetRecentTasks().First(t => t.TaskId == id2);
        Assert.Equal(SctTaskStatus.Cancelled, history.Status);
    }

    [Fact]
    public void 队列_删除仅限排队中_不存在与执行中拒绝()
    {
        var engine = Create();
        var id1 = engine.SubmitTask(SctTaskType.Production, "PL-001", "注册入队");
        var id2 = engine.SubmitTask(SctTaskType.Production, "PL-002", "注册入队");

        // 执行中不可删除（FIFO 出队 PL-001 变执行中）
        Assert.True(engine.TryDequeue(out var running));
        Assert.False(engine.RemoveQueuedTask(running.TaskId));

        // 不存在的任务号拒绝
        Assert.False(engine.RemoveQueuedTask(9999));

        // 其余排队任务不受影响；完成后执行中任务入历史
        engine.CompleteTask(running.TaskId, true, null);
        Assert.Equal(new[] { id2 }, engine.GetQueuedTasks().Select(t => t.TaskId).ToList());
    }

    [Fact]
    public void 队列_按样品移除排队任务_仅删该样品且不影响他人()
    {
        var engine = Create();
        var idA1 = engine.SubmitTask(SctTaskType.Production, "PL-A", "注册入队");
        var idA2 = engine.SubmitTask(SctTaskType.HeightMeasure, "PL-A", "测高联动");
        var idB1 = engine.SubmitTask(SctTaskType.Production, "PL-B", "注册入队");

        // 移除 PL-A 的全部排队任务；PL-B 不受影响
        Assert.Equal(2, engine.RemoveQueuedTasksBySample("PL-A"));
        Assert.Equal(new[] { idB1 }, engine.GetQueuedTasks().Select(t => t.TaskId).ToList());

        // 被移除任务留痕 Cancelled；不存在样品返回 0
        Assert.All(engine.GetRecentTasks().Where(t => t.SampleId == "PL-A"),
            t => Assert.Equal(SctTaskStatus.Cancelled, t.Status));
        Assert.Equal(0, engine.RemoveQueuedTasksBySample("PL-NONE"));
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
        Assert.False(sample.IsActive);                          // 需测高 → IsActive 随状态联动停用
        Assert.True(engine.TryDequeue(out var task));
        Assert.Equal(SctTaskType.HeightMeasure, task.TaskType);
        Assert.Equal("SUS-001", task.SampleId);
        Assert.Equal(SctStrategyEngine.PriorityHeightMeasure, task.Priority);
    }

    [Fact]
    public void 测高联动_厚度充足恢复正常()
    {
        _samples.Store.Add(MakeSample("SUS-001", status: SampleStatus.NeedMeasure, thickness: 9, minThickness: 8));
        _samples.GetById("SUS-001")!.IsActive = false;          // 需测高期间停用
        var engine = Create();

        engine.NotifyHeightMeasured("SUS-001", thickness: 8.5);

        var sample = _samples.GetById("SUS-001")!;
        Assert.Equal(SampleStatus.Normal, sample.Status);       // 清 NeedMeasure
        Assert.True(sample.IsActive);                           // 测高合格 → IsActive 随状态联动恢复启用
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
        Assert.False(sample.IsActive);                          // 过薄禁用 → IsActive 随状态联动停用
        Assert.Contains(_errors.Collected, c => c.Code == ErrorCodes.SctThicknessBelowMinimum);
        // 留痕已并入编排层完成日志行（一行一任务），引擎不再单独写日志
        Assert.DoesNotContain(_logs.Inserted, l => l.OperationType == "HeightMeasure");
    }

    // ==================== 兼任孪生阈值联动 ====================

    [Fact]
    public void 兼任孪生_主样达阈值孪生同步置NeedMeasure并各插测高任务()
    {
        var main = MakeSample("HC-01", SampleType.Control, usage: 50, maxUsage: 50);
        main.PairSampleId = "HC-01-T";
        var twin = MakeSample("HC-01-T", SampleType.TypeStd, alloy: "6063", usage: 50, maxUsage: 50);
        twin.PairSampleId = "HC-01";
        _samples.Store.Add(main);
        _samples.Store.Add(twin);
        var engine = Create();

        engine.NotifyAnalysisCompleted("HC-01");    // 检查完成（Workflow 已落库并拉平孪生次数）

        Assert.Equal(SampleStatus.NeedMeasure, _samples.GetById("HC-01")!.Status);
        Assert.Equal(SampleStatus.NeedMeasure, _samples.GetById("HC-01-T")!.Status);
        Assert.True(engine.TryDequeue(out var t1));
        Assert.Equal(SctTaskType.HeightMeasure, t1.TaskType);
        engine.CompleteTask(t1.TaskId, true, null);
        Assert.True(engine.TryDequeue(out var t2));
        Assert.Equal(SctTaskType.HeightMeasure, t2.TaskType);
        Assert.Equal("HC-01-T", t2.SampleId);       // 孪生独立测高任务，各自闭环
    }

    [Fact]
    public void 兼任孪生_孪生已NeedMeasure不重复提交测高()
    {
        var main = MakeSample("HC-01", SampleType.Control, usage: 50, maxUsage: 50);
        main.PairSampleId = "HC-01-T";
        _samples.Store.Add(main);
        _samples.Store.Add(MakeSample("HC-01-T", SampleType.TypeStd, alloy: "6063", status: SampleStatus.NeedMeasure));
        var engine = Create();

        engine.NotifyAnalysisCompleted("HC-01");

        Assert.True(engine.TryDequeue(out var task));
        Assert.Equal("HC-01", task.SampleId);       // 仅主样一条测高任务
        engine.CompleteTask(task.TaskId, true, null);
        Assert.False(engine.TryDequeue(out _));
    }

    [Fact]
    public void 兼任孪生_孪生次数未达阈值不联动()
    {
        var main = MakeSample("HC-01", SampleType.Control, usage: 50, maxUsage: 50);
        main.PairSampleId = "HC-01-T";
        _samples.Store.Add(main);
        _samples.Store.Add(MakeSample("HC-01-T", SampleType.TypeStd, alloy: "6063"));
        var engine = Create();

        engine.NotifyAnalysisCompleted("HC-01");

        Assert.Equal(SampleStatus.NeedMeasure, _samples.GetById("HC-01")!.Status);
        Assert.Equal(SampleStatus.Normal, _samples.GetById("HC-01-T")!.Status);     // 孪生保持正常
        Assert.True(engine.TryDequeue(out var task));
        Assert.Equal("HC-01", task.SampleId);
        engine.CompleteTask(task.TaskId, true, null);
        Assert.False(engine.TryDequeue(out _));
    }

    // ==================== 检查样与类型标准化样混用 ====================

    /// <summary>按数量插入路径辅助：配置 ByCount=1 并触发一次生产完成，出队控制检查任务。</summary>
    private SctTask EnqueueControlCheckViaProduction(string productionId)
    {
        _policy.Current.ControlInsertMode = ControlInsertMode.ByCount;
        _policy.Current.ControlInsertCount = 1;
        _samples.Store.Add(MakeSample(productionId, SampleType.Production));
        var engine = Create();
        engine.NotifyAnalysisCompleted(productionId);
        Assert.True(engine.TryDequeue(out var task));
        Assert.Equal(SctTaskType.ControlCheck, task.TaskType);
        return task;
    }

    [Fact]
    public void 混用_控制检查按使用次数最少选取且并列优先本类型()
    {
        _policy.Current.ControlTypeStdMixed = true;
        // 检查样 usage=5，类型标准化样 usage=5：并列 → 优先本类型 Control
        _samples.Store.Add(MakeSample("HC-01", SampleType.Control, usage: 5));
        _samples.Store.Add(MakeSample("TS-01", SampleType.TypeStd, alloy: "6063", usage: 5));
        var task = EnqueueControlCheckViaProduction("P-MIX");
        Assert.Equal("HC-01", task.SampleId);

        // 检查样 usage 提到 10：类型标准化样次数更少 → 混用选中 TS-01
        _samples.GetById("HC-01")!.UsageCount = 10;
        var task2 = EnqueueControlCheckViaProduction("P-MIX2");
        Assert.Equal("TS-01", task2.SampleId);
    }

    [Fact]
    public void 混用_无可用检查样时回退选类型标准化样()
    {
        _policy.Current.ControlTypeStdMixed = true;
        _samples.Store.Add(MakeSample("TS-01", SampleType.TypeStd, alloy: "6063"));
        var task = EnqueueControlCheckViaProduction("P-MIX");
        Assert.Equal("TS-01", task.SampleId);       // 唯一候选为 TypeStd，混用放行
    }

    [Fact]
    public void 未混用_无可用检查样仍报警不跨类型选样()
    {
        _samples.Store.Add(MakeSample("TS-01", SampleType.TypeStd, alloy: "6063"));
        _policy.Current.ControlInsertMode = ControlInsertMode.ByCount;
        _policy.Current.ControlInsertCount = 1;
        _samples.Store.Add(MakeSample("P-MIX", SampleType.Production));
        var engine = Create();

        engine.NotifyAnalysisCompleted("P-MIX");
        Assert.False(engine.TryDequeue(out _));
        Assert.Contains(_errors.Collected, c => c.Code == ErrorCodes.SctSettingSampleUnavailable);
    }

    [Fact]
    public void 混用_无匹配合金类型标准化样时回退选检查样()
    {
        _policy.Current.ControlTypeStdMixed = true;
        _samples.Store.Add(MakeSample("HC-01", SampleType.Control));
        var engine = Create();

        engine.NotifyAlloySwitched("3003");         // 无 TypeStd 匹配 3003 → 混用回退选 Control
        Assert.True(engine.TryDequeue(out var task));
        Assert.Equal(SctTaskType.TypeStandardize, task.TaskType);
        Assert.Equal("HC-01", task.SampleId);
    }

    [Fact]
    public void 未混用_无匹配合金类型标准化样仍报警()
    {
        _samples.Store.Add(MakeSample("HC-01", SampleType.Control));
        var engine = Create();

        engine.NotifyAlloySwitched("3003");
        Assert.False(engine.TryDequeue(out _));
        Assert.Contains(_errors.Collected, c => c.Code == ErrorCodes.SctTypeStdUnavailable);
    }

    [Fact]
    public void 混用_计时按任务用途归属()
    {
        _policy.Current.ControlTypeStdMixed = true;
        _policy.Current.TypeStandardizePeriodEnabled = true;
        _policy.Current.TypeStandardizePeriodDays = 7;
        _samples.Store.Add(MakeSample("TS-01", SampleType.TypeStd, alloy: "6063"));
        _samples.Store.Add(MakeSample("HC-01", SampleType.Control));
        var engine = Create();

        // TypeStd 样承担控制检查：完成刷新检查计时而非类型标准化期限
        engine.SubmitTask(SctTaskType.ControlCheck, "TS-01", "混用控制检查");
        Assert.True(engine.TryDequeue(out var t1));
        engine.CompleteTask(t1.TaskId, true, null);
        engine.NotifyAnalysisCompleted("TS-01", SctTaskType.ControlCheck);

        // Control 样承担类型标准化：完成刷新类型标准化期限
        engine.SubmitTask(SctTaskType.TypeStandardize, "HC-01", "混用类型标准化");
        Assert.True(engine.TryDequeue(out var t2));
        engine.CompleteTask(t2.TaskId, true, null);
        engine.NotifyAnalysisCompleted("HC-01", SctTaskType.TypeStandardize);

        // 类型标准化期限已按任务用途刷新：到期时刻 ≈ now + 7 天（若误刷成检查计时则不会出现该期限）
        var countdown = engine.GetStandardizeCountdown();
        Assert.NotNull(countdown);
        Assert.NotNull(countdown.TypeStdDueTime);
        var days = (countdown.TypeStdDueTime!.Value - _time.GetLocalNow().DateTime).TotalDays;
        Assert.True(days is > 6.9 and <= 7.01, $"类型标准化期限应为 now+7 天，实际 {days:0.##} 天");
    }

    [Fact]
    public void 未混用_手动下发达任务类型与样品类型不匹配被拒()
    {
        _samples.Store.Add(MakeSample("TS-01", SampleType.TypeStd, alloy: "6063"));
        var engine = Create();

        var taskId = engine.SubmitTask(SctTaskType.ControlCheck, "TS-01", "手动触发检查");
        Assert.Equal(0, taskId);                    // 拒绝入队
        Assert.False(engine.TryDequeue(out _));
        Assert.Contains(_errors.Collected, c => c.Code == ErrorCodes.SctTaskSampleTypeMismatch);

        // 勾选混用后放行
        _policy.Current.ControlTypeStdMixed = true;
        var taskId2 = engine.SubmitTask(SctTaskType.ControlCheck, "TS-01", "手动触发检查");
        Assert.NotEqual(0, taskId2);
        Assert.True(engine.TryDequeue(out var task));
        Assert.Equal("TS-01", task.SampleId);
    }
}
