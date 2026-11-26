namespace SpectroscopicAnalysis.Host.SctStrategy;

/// <summary>
/// 定期标准化/类型标准化期限倒计时快照（生产监控 KPI 卡展示用）。
/// DueTime 为 null 语义分两种：对应开关未启用（IsEnabled=false）或从未做过且视为已到期（DueTime=当前时刻）。
/// </summary>
public sealed record SctStandardizeCountdown(
    bool PeriodicEnabled,
    DateTime? StandardizeDueTime,
    bool TypeStdEnabled,
    DateTime? TypeStdDueTime);
