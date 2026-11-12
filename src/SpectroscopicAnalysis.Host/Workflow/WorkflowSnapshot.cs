namespace SpectroscopicAnalysis.Host.Workflow;

/// <summary>编排器运行快照（Dashboard「当前任务卡 + 任务队列卡」数据源——编排设计文档 §11）。</summary>
public sealed record WorkflowSnapshot(
    int? CurrentTaskId,
    string CurrentTaskType,
    string? CurrentSampleId,
    string TaskState,
    string? SctSubState,
    bool IsRunning,
    IReadOnlyDictionary<string, bool> LockOccupancy);

/// <summary>编排状态变化事件参数（上抛 UI，经调度器切线程——MVVM 分层）。</summary>
public sealed class WorkflowStateChangedEventArgs(WorkflowSnapshot snapshot) : EventArgs
{
    public WorkflowSnapshot Snapshot { get; } = snapshot;
}

/// <summary>流程编排器接口（编排设计文档 §11 草案落地）。</summary>
public interface IWorkflowOrchestrator : IAsyncDisposable
{
    /// <summary>当前运行快照。</summary>
    WorkflowSnapshot Snapshot { get; }

    /// <summary>启动主循环（出队 → 预检 → 设备编排 → 回执）。</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>优雅停止：完成当前步后暂停主循环（不清任务队列）。</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>急停（9001）：终止当前任务 + 引擎 EmergencyReset（编排设计文档 §7.4）。</summary>
    Task EmergencyStopAsync(string reason);

    /// <summary>状态变化事件。</summary>
    event EventHandler<WorkflowStateChangedEventArgs>? StateChanged;
}
