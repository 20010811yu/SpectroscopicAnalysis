namespace SpectroscopicAnalysis.Host.LoggingAlarm;

/// <summary>报警记录（内存报警表条目；进程重启后清零，后续 D9 报警面板复用）。</summary>
public sealed class AlarmRecord
{
    /// <summary>报警自增标识（进程内唯一，确认单条用）。</summary>
    public long AlarmId { get; init; }

    /// <summary>报警时间（本地时间）。</summary>
    public DateTime Time { get; init; }

    /// <summary>统一错误码（开发规范 11.1）。</summary>
    public int ErrorCode { get; init; }

    /// <summary>错误消息。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>样品上下文（无关联为 null）。</summary>
    public string? SampleId { get; init; }

    /// <summary>是否已确认。</summary>
    public bool IsAcknowledged { get; set; }
}
