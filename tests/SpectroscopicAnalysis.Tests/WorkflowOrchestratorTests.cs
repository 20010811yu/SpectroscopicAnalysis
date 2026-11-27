using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.DeviceInterface;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.SampleManager;
using SpectroscopicAnalysis.Host.SctStrategy;
using SpectroscopicAnalysis.Host.Workflow;

namespace SpectroscopicAnalysis.Tests;

/// <summary>
/// WorkflowOrchestrator 编排测试（编排设计文档 §12）：预检拦截、生产/SCT/测高任务编排、
/// 引擎回喂、急停清队，Fake 仓储 + Stub 设备网关（可注入失败场景）。
/// </summary>
public sealed class WorkflowOrchestratorTests
{
    private readonly FakeSctPolicyStore _policy = new();
    private readonly FakeSampleRepository _samples = new();
    private readonly FakeProductionSampleRepository _production = new();
    private readonly FakeOperationLogRepository _logs = new();
    private readonly FakeErrorCollector _errors = new();
    private readonly StubDeviceGateway _gateway = new();

    /// <summary>快轮询配置（测试无需等 500ms；任务时长补足同归零）。</summary>
    private static WorkflowOptions FastOptions() => new()
    {
        PollIntervalMs = 10,
        TaskDurationMs = 0,
        EngineTickIntervalMs = 600_000,
        LockTimeoutMs = 1_000,
        StepTimeoutMs = 1_000,
    };

    /// <summary>创建引擎 + 共享该实例的编排器（同一队列，与真实 DI 装配一致）。</summary>
    private (SctStrategyEngine Engine, WorkflowOrchestrator Orchestrator) Create()
    {
        var engine = new SctStrategyEngine(_policy, _samples, _logs, _errors);
        var orchestrator = new WorkflowOrchestrator(
            engine, _gateway, _gateway, _gateway, _gateway, _logs, _samples, _production, _errors, new SystemLogCollector(),
            new SampleDataChangeNotifier(), FastOptions());
        return (engine, orchestrator);
    }

    /// <summary>轮询等待条件成立（Stub 任务即时完成，10ms 步长内应达成）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    private static SctSample MakeSample(
        string id,
        string type = SampleType.SettingUp,
        string status = SampleStatus.Normal,
        double thickness = 10,
        double minThickness = 8) => new()
    {
        SampleId = id,
        SampleName = id,
        SampleType = type,
        Status = status,
        CurrentThickness = thickness,
        MinThickness = minThickness,
        MaxUsageCount = 50,
        IsActive = true,
    };

    // ==================== 预检 ====================

    [Fact]
    public async Task 预检未通过_任务失败并报警不入设备()
    {
        _gateway.SafetyOk = false;                          // 模拟安全门打开
        _samples.Store.Add(MakeSample("SUS-001"));
        var (engine, orchestrator) = Create();
        engine.SubmitTask(SctTaskType.Standardize, "SUS-001", "人工触发");

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _errors.Collected.Count > 0);
        await orchestrator.StopAsync(CancellationToken.None);

        Assert.Contains(_errors.Collected, c => c.Message.Contains("预检未通过"));
        Assert.Empty(_gateway.SentCycles);                  // 预检拦截，不下发设备
        Assert.Contains(_logs.Store, l => l.TriggerReason == "预检未通过，任务失败");
    }

    // ==================== SCT 任务编排 ====================

    [Fact]
    public async Task 标准化任务_ID_CYCLE映射与回喂引擎()
    {
        _samples.Store.Add(MakeSample("SUS-001"));
        var usageBefore = _samples.GetById("SUS-001")!.UsageCount;
        var (engine, orchestrator) = Create();
        engine.SubmitTask(SctTaskType.Standardize, "SUS-001", "人工触发");

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _logs.Store.Any(l => l.TriggerReason == "人工触发"));
        await orchestrator.StopAsync(CancellationToken.None);

        Assert.Contains(_gateway.SentCycles, c => c.IdCycle == 2 && c.SampleId == "SUS-001");   // ID_CYCLE=2 标准化
        // 一行一任务：完成行触发原因=下发原因，且带开始时间（指令下发时刻）
        Assert.Contains(_logs.Store, l => l.TriggerReason == "人工触发" && l.ResultStatus == "Success" && l.StartTime is not null);
        Assert.Equal(1, _logs.Store.Count(l => l.OperationType == "Standardization" && l.TriggerReason == "人工触发"));   // 入队行已并入完成行
        // 激发后档案落库：使用次数+1、上次使用时间刷新（修复台账「上次使用」不更新缺口）
        var sample = _samples.GetById("SUS-001")!;
        Assert.Contains("SUS-001", _samples.SparkedIds);
        Assert.Equal(usageBefore + 1, sample.UsageCount);
        Assert.NotNull(sample.LastUseTime);
        // Analysis_Result 仅归档生产样（表外键引用 Production_Sample）；SCT 样审计在 SCT_OperationLog
        Assert.Empty(_production.GetResults("SUS-001"));
        // 引擎回喂：标准化完成记录时间 → 定时标准化不再立即到期
        engine.OnTick();
        Assert.False(engine.TryDequeue(out _));
    }

    [Fact]
    public async Task 控制检查任务_结果回喂超差闭环()
    {
        _policy.Current.AutoStandardizeOnControlFail = true;
        _samples.Store.Add(MakeSample("SUS-001"));
        var (engine, orchestrator) = Create();

        // 直接激发失败（Stub）→ 判定超差 → 引擎闭环：标准化入队并由编排器自动执行
        _gateway.SparkSucceeds = false;
        engine.SubmitTask(SctTaskType.ControlCheck, "CTL-001", "人工触发");
        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _logs.Store.Any(l => l.OperationType == "Standardization" && l.TriggerReason == "检查样超差"));
        await orchestrator.StopAsync(CancellationToken.None);

        // 闭环证据：超差日志 + 闭环标准化完成行（触发原因=下发原因「检查样超差」，入队行已并入完成行）+ 两次下发
        Assert.Contains(_logs.Store, l => l.OperationType == "ControlCheck" && l.IsPass == false);
        Assert.Contains(_logs.Store, l => l.OperationType == "Standardization" && l.TriggerReason == "检查样超差" && l.StartTime is not null);
        Assert.Contains(_gateway.SentCycles, c => c.IdCycle == 3 && c.SampleId == "CTL-001");
    }

    // ==================== 测高任务 ====================

    [Fact]
    public async Task 测高任务_厚度充足恢复正常()
    {
        _samples.Store.Add(MakeSample("SUS-001", status: SampleStatus.NeedMeasure, thickness: 9, minThickness: 8));
        _gateway.MeasuredThickness = 8.5;
        var (engine, orchestrator) = Create();
        engine.SubmitTask(SctTaskType.HeightMeasure, "SUS-001", "人工测高触发");

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _samples.GetById("SUS-001")!.Status == SampleStatus.Normal);
        await orchestrator.StopAsync(CancellationToken.None);

        var sample = _samples.GetById("SUS-001")!;
        Assert.Equal(8.5, sample.CurrentThickness);             // 回填厚度
        Assert.Contains(_gateway.MeasuredHeights, id => id == "SUS-001");
    }

    [Fact]
    public async Task 测高任务_低于最小厚度禁用并报警()
    {
        _samples.Store.Add(MakeSample("SUS-001", status: SampleStatus.NeedMeasure, thickness: 9, minThickness: 8));
        _gateway.MeasuredThickness = 4;
        var (engine, orchestrator) = Create();
        engine.SubmitTask(SctTaskType.HeightMeasure, "SUS-001", "人工测高触发");

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _errors.Collected.Any(c => c.Code == ErrorCodes.SctThicknessBelowMinimum));
        await orchestrator.StopAsync(CancellationToken.None);

        Assert.Equal(SampleStatus.Disabled, _samples.GetById("SUS-001")!.Status);
    }

    // ==================== SCT 操作前置测高（需测高样兜底候选） ====================

    [Fact]
    public async Task 标准化任务_需测高样前置测高合格后继续执行()
    {
        _samples.Store.Add(MakeSample("SUS-A", status: SampleStatus.NeedMeasure, thickness: 9, minThickness: 8));
        _gateway.MeasuredThickness = 8.5;                   // 测高合格（≥ 最小厚度）
        var (engine, orchestrator) = Create();
        engine.SubmitTask(SctTaskType.Standardize, "SUS-A", "人工触发");

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _samples.GetById("SUS-A")!.UsageCount > 0);
        await orchestrator.StopAsync(CancellationToken.None);

        // 先测高后标准化：测高指令与 ID_CYCLE=2 均下发，测高合格恢复 Normal 后原操作完成
        Assert.Contains(_gateway.MeasuredHeights, id => id == "SUS-A");
        Assert.Contains(_gateway.SentCycles, c => c.IdCycle == 2 && c.SampleId == "SUS-A");
        Assert.Equal(SampleStatus.Normal, _samples.GetById("SUS-A")!.Status);
        Assert.Contains(_logs.Store, l => l.TriggerReason == "执行前测高（需测高样强制前置）");
    }

    [Fact]
    public async Task 标准化任务_前置测高不合格禁用并任务内换样继续()
    {
        _samples.Store.Add(MakeSample("SUS-A", status: SampleStatus.NeedMeasure, thickness: 9, minThickness: 8));
        _samples.Store.Add(MakeSample("SUS-B"));            // 替代样（Normal）
        _gateway.MeasuredThickness = 4;                     // 测高不合格（< 最小厚度 8）
        var (engine, orchestrator) = Create();
        engine.SubmitTask(SctTaskType.Standardize, "SUS-A", "人工触发");

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.GetRecentTasks().Any(t => t.Reason.Contains("人工触发") && t.Status == SctTaskStatus.Completed));
        await orchestrator.StopAsync(CancellationToken.None);

        // SUS-A 测高不合格禁用；同一任务换 SUS-B 继续并成功完成（任务不失败）
        Assert.Equal(SampleStatus.Disabled, _samples.GetById("SUS-A")!.Status);
        Assert.Contains(_gateway.SentCycles, c => c.IdCycle == 2 && c.SampleId == "SUS-B");
        Assert.DoesNotContain(_gateway.SentCycles, c => c.IdCycle == 2 && c.SampleId == "SUS-A");
        Assert.Contains(_logs.Store, l => l.TriggerReason != null && l.TriggerReason.Contains("测高不合格禁用"));
        var task = engine.GetRecentTasks().Single(t => t.Reason.Contains("人工触发"));
        Assert.Equal(SctTaskStatus.Completed, task.Status);
        Assert.Equal("SUS-B", task.SampleId);               // 任务记录已换样
        Assert.Contains("前置测高不合格禁用", task.Reason);   // 换样原因留痕在任务记录
    }

    [Fact]
    public async Task 标准化任务_前置测高不合格且无替代样_任务失败()
    {
        _samples.Store.Add(MakeSample("SUS-A", status: SampleStatus.NeedMeasure, thickness: 9, minThickness: 8));
        _gateway.MeasuredThickness = 4;                     // 测高不合格且无其他可用样
        var (engine, orchestrator) = Create();
        engine.SubmitTask(SctTaskType.Standardize, "SUS-A", "人工触发");

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _errors.Collected.Any(c => c.Message.Contains("无替代样品")));
        await orchestrator.StopAsync(CancellationToken.None);

        Assert.Equal(SampleStatus.Disabled, _samples.GetById("SUS-A")!.Status);
        Assert.DoesNotContain(_gateway.SentCycles, c => c.IdCycle == 2);    // 未激发任何标准化
        var task = engine.GetRecentTasks().Single(t => t.Reason.Contains("人工触发"));
        Assert.Equal(SctTaskStatus.Failed, task.Status);
    }

    // ==================== 急停 ====================

    [Fact]
    public async Task 急停_队列清空并主循环退出()
    {
        _samples.Store.Add(MakeSample("SUS-001"));
        _samples.Store.Add(MakeSample("SUS-002"));
        var (engine, orchestrator) = Create();
        engine.SubmitTask(SctTaskType.Standardize, "SUS-001", "人工触发");
        engine.SubmitTask(SctTaskType.HeightMeasure, "SUS-002", "人工测高触发");

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => orchestrator.Snapshot.IsRunning);
        await orchestrator.EmergencyStopAsync("测试急停");

        Assert.False(engine.TryDequeue(out _));                 // 引擎队列清空（EmergencyReset）
        Assert.False(orchestrator.Snapshot.IsRunning);          // 主循环退出
        Assert.Contains(_errors.Collected, c => c.Code == ErrorCodes.EmergencyStop);
        Assert.Equal(TaskState.Idle, orchestrator.Snapshot.TaskState);
    }

    // ==================== Snapshot ====================

    [Fact]
    public async Task 生产任务_激发结果归档生产样()
    {
        _production.Store.Add(new ProductionSample
        {
            SampleId = "P-001",
            SampleName = "生产样 1",
            SampleType = "Production",
            AssociatedMethod = "M-1",
            Status = ProductionStatus.Pending,
            CreatedTime = DateTime.Now,
        });
        var (engine, orchestrator) = Create();
        engine.SubmitTask(SctTaskType.Production, "P-001", "样品注册自动入队");

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _production.GetResults("P-001").Any());
        await orchestrator.StopAsync(CancellationToken.None);

        // 激发结果归档：原始强度按协议原文落库（Analysis_Result 外键引用 Production_Sample）
        var result = _production.GetResults("P-001").Single();
        Assert.Equal("1000", result.RawIntensity);
        Assert.Equal(AnalysisResultStatus.Success, result.ResultStatus);
        Assert.True(result.IsPass);
        Assert.NotNull(result.OperationTime);
    }

    [Fact]
    public async Task 任务时长_执行过程补足到配置时长()
    {
        // 时长 300ms：单个 Stub 生产任务从开始到结果归档应历时 ≥300ms（模拟真实流程耗时）
        var options = FastOptions();
        options.TaskDurationMs = 300;
        _production.Store.Add(new ProductionSample
        {
            SampleId = "P-001", SampleName = "生产样 1", SampleType = "Production",
            AssociatedMethod = "M-1", Status = ProductionStatus.Pending, CreatedTime = DateTime.Now,
        });
        var engine = new SctStrategyEngine(_policy, _samples, _logs, _errors);
        var orchestrator = new WorkflowOrchestrator(
            engine, _gateway, _gateway, _gateway, _gateway, _logs, _samples, _production, _errors, new SystemLogCollector(),
            new SampleDataChangeNotifier(), options);
        engine.SubmitTask(SctTaskType.Production, "P-001", "样品注册自动入队");

        var start = DateTime.UtcNow;
        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _production.GetResults("P-001").Any());
        await orchestrator.StopAsync(CancellationToken.None);
        var elapsed = DateTime.UtcNow - start;

        Assert.True(elapsed.TotalMilliseconds >= 300, $"任务耗时 {elapsed.TotalMilliseconds}ms，未补足配置时长");
        Assert.Equal(ProductionStatus.Completed, _production.Store.Single(p => p.SampleId == "P-001").Status);
    }

    [Fact]
    public async Task 任务时长_标准化执行历时补足到配置时长()
    {
        // 时长 300ms：标准化任务从开始到完成日志落笔应历时 ≥300ms（与生产任务一致，流程末尾落结果）
        var options = FastOptions();
        options.TaskDurationMs = 300;
        _samples.Store.Add(MakeSample("SUS-001"));
        var engine = new SctStrategyEngine(_policy, _samples, _logs, _errors);
        var orchestrator = new WorkflowOrchestrator(
            engine, _gateway, _gateway, _gateway, _gateway, _logs, _samples, _production, _errors, new SystemLogCollector(),
            new SampleDataChangeNotifier(), options);
        engine.SubmitTask(SctTaskType.Standardize, "SUS-001", "人工触发");

        var start = DateTime.UtcNow;
        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _logs.Store.Any(l => l.TriggerReason == "人工触发"));
        await orchestrator.StopAsync(CancellationToken.None);
        var elapsed = DateTime.UtcNow - start;

        Assert.True(elapsed.TotalMilliseconds >= 300, $"标准化耗时 {elapsed.TotalMilliseconds}ms，未补足配置时长");
        Assert.Contains(_logs.Store, l => l.TriggerReason == "人工触发" && l.ResultStatus == "Success");   // 完成后写执行日志
    }

    [Fact]
    public void 快照_初始态与锁占用()
    {
        var (_, orchestrator) = Create();
        var snapshot = orchestrator.Snapshot;

        Assert.Null(snapshot.CurrentTaskId);
        Assert.Equal(TaskState.Idle, snapshot.TaskState);
        Assert.False(snapshot.IsRunning);
        Assert.Contains(WorkflowLockName.Robot, snapshot.LockOccupancy.Keys);
    }

    // ==================== 生产前合金切换前置类型标准化 ====================

    [Fact]
    public async Task 无匹配合金类型标准化样_生产样跳过置待分析并继续下一任务()
    {
        // 生产样 1（合金 9999 无匹配类型标准化样）→ 生产样 2（合金 3003 有匹配）：样 1 跳过生产置待分析，样 2 正常生产
        _production.Store.Add(new ProductionSample
        {
            SampleId = "P-9999", SampleName = "生产样 9999", SampleType = "Production",
            AlloyType = "9999", AssociatedMethod = "M-1", Status = ProductionStatus.Pending, CreatedTime = DateTime.Now,
        });
        _production.Store.Add(new ProductionSample
        {
            SampleId = "P-3003", SampleName = "生产样 3003", SampleType = "Production",
            AlloyType = "3003", AssociatedMethod = "M-1", Status = ProductionStatus.Pending, CreatedTime = DateTime.Now,
        });
        var typeStd3003 = MakeSample("TYP-3003", type: SampleType.TypeStd);
        typeStd3003.AlloyType = "3003";
        _samples.Store.Add(typeStd3003);

        var (engine, orchestrator) = Create();
        engine.SubmitTask(SctTaskType.Production, "P-9999", "样品注册自动入队");
        engine.SubmitTask(SctTaskType.Production, "P-3003", "样品注册自动入队");

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _production.GetResults("P-3003").Any());
        await orchestrator.StopAsync(CancellationToken.None);

        // P-9999 不进生产流程：无 ID_CYCLE=1、报警 5103、状态置待分析、任务移出队列
        Assert.DoesNotContain(_gateway.SentCycles, c => c.SampleId == "P-9999");
        Assert.Contains(_errors.Collected, c => c.Code == ErrorCodes.SctTypeStdUnavailable
            && c.SampleId == "P-9999");
        Assert.Equal(ProductionStatus.Pending, _production.Store.Single(p => p.SampleId == "P-9999").Status);
        Assert.DoesNotContain(engine.GetRecentTasks(), t => t.SampleId == "P-9999" && t.Status == SctTaskStatus.Completed);

        // P-3003 不受影响：前置类型标准化 + 正常生产完成
        var cycles = _gateway.SentCycles.Select(c => (c.SampleId, c.IdCycle)).ToList();
        Assert.Contains(("TYP-3003", 2), cycles);
        Assert.Contains(("P-3003", 1), cycles);
        Assert.Equal(ProductionStatus.Completed, _production.Store.Single(p => p.SampleId == "P-3003").Status);
    }

    [Fact]
    public async Task 合金切换_生产前先执行匹配合金的类型标准化()
    {
        // 生产样 1（合金 3003）→ 生产样 2（合金 6061）：第二个生产任务前应先对 6061 的类型标准化样执行 ID_CYCLE=2
        _production.Store.Add(new ProductionSample
        {
            SampleId = "P-3003", SampleName = "生产样 3003", SampleType = "Production",
            AlloyType = "3003", AssociatedMethod = "M-1", Status = ProductionStatus.Pending, CreatedTime = DateTime.Now,
        });
        _production.Store.Add(new ProductionSample
        {
            SampleId = "P-6061", SampleName = "生产样 6061", SampleType = "Production",
            AlloyType = "6061", AssociatedMethod = "M-1", Status = ProductionStatus.Pending, CreatedTime = DateTime.Now,
        });
        var typeStd6061 = MakeSample("TYP-6061", type: SampleType.TypeStd);
        typeStd6061.AlloyType = "6061";
        _samples.Store.Add(typeStd6061);

        var (engine, orchestrator) = Create();
        engine.SubmitTask(SctTaskType.Production, "P-3003", "样品注册自动入队");
        engine.SubmitTask(SctTaskType.Production, "P-6061", "样品注册自动入队");

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _production.GetResults("P-6061").Any());
        await orchestrator.StopAsync(CancellationToken.None);

        // 执行次序：P-3003（合金 3003 无匹配类型标准化样）跳过生产 → TYP-6061 前置类型标准化（ID_CYCLE=2）→ P-6061 生产
        var cycles = _gateway.SentCycles.Select(c => (c.SampleId, c.IdCycle)).ToList();
        Assert.Equal(2, cycles.Count);
        Assert.Equal(("TYP-6061", 2), cycles[0]);       // 前置类型标准化（合金匹配 6061）
        Assert.Equal(("P-6061", 1), cycles[1]);
        // P-3003 跳过生产：无 ID_CYCLE=1、5103 报警、状态保持待分析、任务移出队列（不入完成历史）
        Assert.DoesNotContain(_gateway.SentCycles, c => c.SampleId == "P-3003");
        Assert.Contains(_errors.Collected, c => c.Code == ErrorCodes.SctTypeStdUnavailable && c.SampleId == "P-3003");
        Assert.Equal(ProductionStatus.Pending, _production.Store.Single(p => p.SampleId == "P-3003").Status);
        Assert.DoesNotContain(engine.GetRecentTasks(), t => t.SampleId == "P-3003" && t.Status == SctTaskStatus.Completed);
        // 前置类型标准化完成行落库（一行一任务）
        Assert.Contains(_logs.Store, l => l.OperationType == "TypeStandardize" && l.SampleId == "TYP-6061"
            && (l.TriggerReason ?? string.Empty).Contains("前置类型标准化") && l.ResultStatus == "Success");
        // 前置类型标准化经任务队列调度（引擎历史含该任务，状态已完成）而非编排器内联执行
        Assert.Contains(engine.GetRecentTasks(), t => t.TaskType == SctTaskType.TypeStandardize
            && t.SampleId == "TYP-6061" && t.Status == SctTaskStatus.Completed
            && (t.Reason ?? string.Empty).Contains("前置类型标准化"));
        // P-6061 正常完成
        Assert.Equal(ProductionStatus.Completed, _production.Store.Single(p => p.SampleId == "P-6061").Status);
    }

    [Fact]
    public async Task 同合金_生产前不插入类型标准化()
    {
        _production.Store.Add(new ProductionSample
        {
            SampleId = "P-A", SampleName = "生产样 A", SampleType = "Production",
            AlloyType = "3003", AssociatedMethod = "M-1", Status = ProductionStatus.Pending, CreatedTime = DateTime.Now,
        });
        _production.Store.Add(new ProductionSample
        {
            SampleId = "P-B", SampleName = "生产样 B", SampleType = "Production",
            AlloyType = "3003", AssociatedMethod = "M-1", Status = ProductionStatus.Pending, CreatedTime = DateTime.Now,
        });
        var typeStd3003 = MakeSample("TYP-3003", type: SampleType.TypeStd);
        typeStd3003.AlloyType = "3003";
        _samples.Store.Add(typeStd3003);

        // 从日志恢复最近一次类型标准化合金基准：TYP-3003 为 3003 → 同合金直接生产
        _logs.Store.Add(new SctOperationLog
        {
            SampleId = "TYP-3003", OperationType = "TypeStandardize", TriggerReason = "手动触发",
            OperationTime = DateTime.Now.AddMinutes(-30).ToString("yyyy-MM-dd HH:mm:ss"), ResultStatus = "Success",
        });

        var (engine, orchestrator) = Create();
        engine.SubmitTask(SctTaskType.Production, "P-A", "样品注册自动入队");
        engine.SubmitTask(SctTaskType.Production, "P-B", "样品注册自动入队");

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _production.GetResults("P-B").Any());
        await orchestrator.StopAsync(CancellationToken.None);

        // 同合金直接生产：仅两次 ID_CYCLE=1，无前置类型标准化（种子基准日志除外）
        Assert.All(_gateway.SentCycles, c => Assert.Equal(1, c.IdCycle));
        Assert.Equal(2, _gateway.SentCycles.Count);
        Assert.DoesNotContain(_logs.Store, l => l.OperationType == "TypeStandardize" && (l.TriggerReason ?? string.Empty).Contains("前置"));
    }

    [Fact]
    public async Task 基准未知_首个生产样前置类型标准化()
    {
        _production.Store.Add(new ProductionSample
        {
            SampleId = "P-A", SampleName = "生产样 A", SampleType = "Production",
            AlloyType = "3003", AssociatedMethod = "M-1", Status = ProductionStatus.Pending, CreatedTime = DateTime.Now,
        });
        var typeStd3003 = MakeSample("TYP-3003", type: SampleType.TypeStd);
        typeStd3003.AlloyType = "3003";
        _samples.Store.Add(typeStd3003);

        // 日志无生产历史（基准未知）：首个生产样也须先做本次合金的类型标准化再生产
        var (engine, orchestrator) = Create();
        engine.SubmitTask(SctTaskType.Production, "P-A", "样品注册自动入队");

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _production.GetResults("P-A").Any());
        await orchestrator.StopAsync(CancellationToken.None);

        // 执行次序：TYP-3003 类型标准化（ID_CYCLE=2）先于 P-A 生产（ID_CYCLE=1）
        var cycles = _gateway.SentCycles.Select(c => (c.SampleId, c.IdCycle)).ToList();
        Assert.Equal(2, cycles.Count);
        Assert.Equal(("TYP-3003", 2), cycles[0]);
        Assert.Equal(("P-A", 1), cycles[1]);
        // 前置类型标准化经任务队列调度且完成行落库
        Assert.Contains(engine.GetRecentTasks(), t => t.TaskType == SctTaskType.TypeStandardize
            && t.SampleId == "TYP-3003" && t.Status == SctTaskStatus.Completed);
        Assert.Contains(_logs.Store, l => l.OperationType == "TypeStandardize" && l.SampleId == "TYP-3003"
            && (l.TriggerReason ?? string.Empty).Contains("前置类型标准化") && l.ResultStatus == "Success");
    }
}
