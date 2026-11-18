using System.IO;

namespace SpectroscopicAnalysis.Host.DeviceInterface;

/// <summary>
/// PLC 数据交互日志（独立文件夹 logs\plc\，按天文件名、只保留最近 3 天）。
/// 条目格式：[时间(毫秒)] [发送/接收] 地址: 内容；新条目置顶（时间倒序）、条目之间明显分隔线。
/// 仅记录 PLC 交互（GNR/铣床不经此通道）；VM 层不感知，设备网关埋点用。
/// </summary>
public sealed class PlcCommLogger : IPlcCommLogger, IDisposable
{
    /// <summary>共用置顶写入器（时间倒序 + 分隔线 + 行数封顶 + 过期清理）。</summary>
    private readonly DeviceLogFileWriter _writer;

    public PlcCommLogger()
    {
        _writer = new DeviceLogFileWriter(
            Path.Combine(AppContext.BaseDirectory, "logs", "plc"),
            filePrefix: "plc",
            retainedDays: DeviceLogFileWriterDefaults.RetainedDays,
            maxLines: DeviceLogFileWriterDefaults.MaxLines);
    }

    /// <inheritdoc />
    public void Log(string direction, string address, string content) =>
        _writer.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{direction}] {address}: {content}");

    /// <summary>无需释放资源（保留 IDisposable 与旧实现兼容）。</summary>
    public void Dispose()
    {
    }
}
