namespace SpectroscopicAnalysis.Host.DeviceInterface;

/// <summary>PLC 任务执行状态（轮询 + 变化检测，任务清单第二节）。</summary>
public static class DeviceTaskState
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

/// <summary>PLC 任务状态快照（MeasuredThickness：测高任务回读的厚度，仅测高流程使用）。</summary>
public sealed record DeviceTaskStatus(string State, string? SampleId, double? MeasuredThickness = null);

/// <summary>安全信号快照（下发前预检，开发规范 11.3：四项必须全部通过）。</summary>
public sealed record SafetyStatus(
    bool SafetyOk,
    bool RobotReady,
    bool SparkStandReady,
    bool SamplePositionMatch)
{
    /// <summary>预检是否全部通过。</summary>
    public bool AllPassed => SafetyOk && RobotReady && SparkStandReady && SamplePositionMatch;
}

/// <summary>激发结果（光谱仪读取，原始强度为协议原文，修正计算归 Correction 模块）。</summary>
public sealed record SparkResult(string SampleId, string RawIntensity, bool Success);

/// <summary>心跳中断事件参数（开发规范第 6 节：中断超阈值进入安全状态）。</summary>
public sealed class HeartbeatLostEventArgs(string reason) : EventArgs
{
    /// <summary>中断原因（超时/连接断开）。</summary>
    public string Reason { get; } = reason;
}

/// <summary>任务结束通知事件参数（SAMPLE_POSITIONED 等握手应答）。</summary>
public sealed class TaskEndEventArgs(string sampleId, string taskState) : EventArgs
{
    /// <summary>样品编号。</summary>
    public string SampleId { get; } = sampleId;

    /// <summary>结束状态（DeviceTaskState 常量）。</summary>
    public string TaskState { get; } = taskState;
}
