using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.DeviceInterface;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.SampleManager;
using SpectroscopicAnalysis.Host.SctStrategy;
using SpectroscopicAnalysis.Host.Workflow;
using Xunit;

namespace SpectroscopicAnalysis.Tests;

/// <summary>
/// 自动插入控制检查回归测试（修复：自动插入的控制块曾瞬间完成、不入队即写日志）：
/// 按时间间隔插入须经策略引擎队列入队 → 编排器执行 → 补足 TaskDurationMs（3 分钟档）→ 完成行落库。
/// </summary>
public sealed class WorkflowControlInsertDurationTests
{
    private readonly FakeSctPolicyStore _policy = new();
    private readonly FakeSampleRepository _samples = new();
    private readonly FakeProductionSampleRepository _production = new();
    private readonly FakeOperationLogRepository _logs = new();
    private readonly FakeErrorCollector _errors = new();
    private readonly StubDeviceGateway _gateway = new();

    [Fact]
    public async Task 按时间间隔自动插入_经队列执行并补足任务时长()
    {
        // 可用的检查样（Control/Normal/启用）：选样通过 → 入队而非直接写日志
        _samples.Store.Add(new SctSample
        {
            SampleId = "CTL-001",
            SampleName = "CTL-001",
            SampleType = SampleType.Control,
            Status = SampleStatus.Normal,
            IsActive = true,
            MaxUsageCount = 50,
        });
        _policy.Current.ControlInsertMode = ControlInsertMode.ByTime;
        _policy.Current.ControlInsertMinutes = 60;      // 无上次记录时视为已到期 → 首个节拍即插入

        var engine = new SctStrategyEngine(_policy, _samples, _logs, _errors);
        var orchestrator = new WorkflowOrchestrator(
            engine, _gateway, _gateway, _gateway, _gateway, _logs, _samples, _production, _errors,
            new SystemLogCollector(), new SampleDataChangeNotifier(),
            new WorkflowOptions
            {
                PollIntervalMs = 10,
                EngineTickIntervalMs = 20,              // 快节拍：启动后立即触发按时间插入
                TaskDurationMs = 300,                   // 缩短档位：验证时长补足语义（生产配置 3 分钟）
                LockTimeoutMs = 1_000,
                StepTimeoutMs = 1_000,
            });

        var startedUtc = DateTime.UtcNow;
        await orchestrator.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_logs.Store.Any(l => l.OperationType == "ControlCheck") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        await orchestrator.StopAsync(CancellationToken.None);
        var elapsed = DateTime.UtcNow - startedUtc;

        // 任务确实经过队列入队（系统日志留痕），而非直接写执行记录
        Assert.Contains(_logs.Store, l => l.OperationType == "ControlCheck" && l.TriggerReason == "按时间间隔插入");
        // 时长补足：执行历时 ≥ TaskDurationMs（非瞬时完成）
        Assert.True(elapsed.TotalMilliseconds >= 300, $"执行历时 {elapsed.TotalMilliseconds}ms，未补足任务时长");
        // 激发指令已下发（真实编排而非仅写日志）
        Assert.Contains(_gateway.SentCycles, c => c.IdCycle == 3 && c.SampleId == "CTL-001");
    }
}
