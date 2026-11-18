using System.IO;
using Serilog;

namespace SpectroscopicAnalysis.Host.DeviceInterface;

/// <summary>PLC 交互方向常量（通信日志方向字段）。</summary>
public static class PlcCommDirection
{
    /// <summary>上位机 → PLC。</summary>
    public const string Send = "发送";

    /// <summary>PLC → 上位机。</summary>
    public const string Receive = "接收";
}

/// <summary>
/// PLC 数据交互日志抽象（独立文件留痕；每条含 PLC 地址/时间/内容/发送或接收方向）。
/// 仅记录 PLC 交互（GNR/铣床不经此通道）；VM 层不感知，设备网关埋点用。
/// </summary>
public interface IPlcCommLogger
{
    /// <summary>记录一条 PLC 交互（direction 用 <see cref="PlcCommDirection"/> 常量）。</summary>
    void Log(string direction, string address, string content);
}

/// <summary>
/// PLC 数据交互日志实现：独立 Serilog 文件 logs\plc\plc-YYYYMMDD.log（独立子文件夹与主日志分离；
/// 按天滚动、只保留最近 3 天，超期自动删除），
/// 输出格式：时间(毫秒) [方向] 地址: 内容，UTF-8 中文可读；不污染主日志（logs/host-.log）配置。
/// Serilog File sink 线程安全，写失败不影响业务。
/// </summary>
public sealed class PlcCommLogger : IPlcCommLogger, IDisposable
{
    /// <summary>日志保留天数（只保留最近 3 天）。</summary>
    public const int RetainedDays = 3;

    /// <summary>专用 Serilog Logger（与主日志独立的管道）。</summary>
    private readonly ILogger _logger;

    public PlcCommLogger()
    {
        _logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(AppContext.BaseDirectory, "logs", "plc", "plc-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedDays,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Direction}] {Address}: {Content}{NewLine}",
                encoding: System.Text.Encoding.UTF8)
            .CreateLogger();
    }

    /// <inheritdoc />
    public void Log(string direction, string address, string content)
    {
        // 结构化属性经 LogContext 注入输出模板（时间戳由 Serilog 自动记录）
        _logger.Information("PLC 交互 {Direction} {Address} {Content}", direction, address, content);
    }

    /// <summary>宿主退出时释放文件句柄并 flush。</summary>
    public void Dispose() => ((IDisposable)_logger).Dispose();
}
