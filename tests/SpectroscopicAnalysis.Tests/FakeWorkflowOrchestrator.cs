using SpectroscopicAnalysis.Host.Workflow;

namespace SpectroscopicAnalysis.Tests;

/// <summary>Fake 编排器：空快照、不事件（主控台队列卡测试用）。</summary>
internal sealed class FakeWorkflowOrchestrator : IWorkflowOrchestrator
{
    public WorkflowSnapshot Snapshot { get; set; } =
        new(null, string.Empty, null, "Idle", null, false, new Dictionary<string, bool>());

    public event EventHandler<WorkflowStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task EmergencyStopAsync(string reason) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
