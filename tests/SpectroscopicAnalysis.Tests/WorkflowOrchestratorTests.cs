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

    /// <summary>快轮询配置（测试无需等 500ms）。</summary>
    private static WorkflowOptions FastOptions() => new()
    {
        PollIntervalMs = 10,
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
        // 激发结果归档：原始强度按协议原文落库（双存缺口补齐）
        var result = _production.GetResults("SUS-001").Single();
        Assert.Equal("1000", result.RawIntensity);
        Assert.Equal(AnalysisResultStatus.Success, result.ResultStatus);
        Assert.True(result.IsPass);
        Assert.NotNull(result.OperationTime);
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
    public void 快照_初始态与锁占用()
    {
        var (_, orchestrator) = Create();
        var snapshot = orchestrator.Snapshot;

        Assert.Null(snapshot.CurrentTaskId);
        Assert.Equal(TaskState.Idle, snapshot.TaskState);
        Assert.False(snapshot.IsRunning);
        Assert.Contains(WorkflowLockName.Robot, snapshot.LockOccupancy.Keys);
    }
}
