namespace SpectroscopicAnalysis.Host.SctStrategy;

/// <summary>
/// 检查样插入进度快照（主控台 KPI「检查样插入倒计时」展示用；纯数据，无逻辑）。
/// ByCount 模式看 ProductionSinceControl / ControlInsertCount；
/// ByTime 模式看 MinutesSinceControl / ControlInsertMinutes（无上次检查记录时已过分钟 = 间隔值，表示到期待触发）。
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
}
