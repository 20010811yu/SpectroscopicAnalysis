namespace SpectroscopicAnalysis.Host.LoggingAlarm;

/// <summary>全流程系统日志条目（内存环形表；供主控台「运行日志」面板消费）。</summary>
public sealed class SystemLogEntry
{
    /// <summary>记录时间（本地时间）。</summary>
    public DateTime Time { get; init; }

    /// <summary>类目（应用/PLC连接/VM方案加载/设备操作/报警 等）。</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>日志内容。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>等级（Info / Warning / Failed；面板按等级着色）。</summary>
    public string Level { get; init; } = "Info";
}

/// <summary>
/// 全流程系统日志抽象：程序级关键事件统一落内存日志表（PLC 连接、VM 方案加载、设备操作、报警等），
/// 与 Serilog 结构化日志并行（业务方两者都写）；主控台「运行日志」面板数据源。
/// </summary>
public interface ISystemLogCollector
{
    /// <summary>写入一条全流程日志（线程安全；时间戳取写入时刻本地时间）。</summary>
    void Log(string category, string message, string level = "Info");

    /// <summary>日志快照（最新在前，封顶保留）。</summary>
    IReadOnlyList<SystemLogEntry> GetEntries();
}
