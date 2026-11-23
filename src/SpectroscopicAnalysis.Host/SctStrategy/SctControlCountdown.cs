namespace SpectroscopicAnalysis.Host.SctStrategy;

/// <summary>
/// 检查样插入进度快照（主控台 KPI「检查样插入倒计时」展示用；纯数据，无逻辑）。
/// ByCount 模式看 ProductionSinceControl / ControlInsertCount；
/// ByTime 模式看 ControlDueTime（到期时刻，供 UI 逐秒推算剩余时间）/ ControlInsertMinutes。
/// </summary>
public sealed record SctControlCountdown
{
    /// <summary>插入模式：ControlInsertMode.ByCount / ByTime。</summary>
    public required string Mode { get; init; }

    /// <summary>本次控制检查后已完成的生产样数（ByCount）。</summary>
    public int ProductionSinceControl { get; init; }

    /// <summary>按数量插入阈值（每 N 个生产样插一次检查样）。</summary>
    public int ControlInsertCount { get; init; }

    /// <summary>距上次控制检查已过分钟数（ByTime；无记录时 = ControlInsertMinutes）。</summary>
    public double MinutesSinceControl { get; init; }

    /// <summary>按时间插入间隔（分钟）。</summary>
    public int ControlInsertMinutes { get; init; }

    /// <summary>ByTime 到期时刻 = 上次检查时间 + 间隔；null = 无上次记录（已到期待触发）/ ByCount 模式。
    /// UI 层据此逐秒推算剩余时间，无需重复查询引擎。</summary>
    public DateTime? ControlDueTime { get; init; }
}
