using System.IO;

namespace SpectroscopicAnalysis.Host.LoggingAlarm;

/// <summary>
/// 全流程系统日志实现：内存环形表（封顶淘汰最旧，防长期运行内存膨胀；进程重启清零）
/// + 文件留痕（logs\system\system-YYYYMMDD.log，按天文件名、保留最近 7 天——参照 PLC 通信日志模式）。
/// 线程安全：Log 可能在任意线程（后台 I/O、托管服务）触发。
/// </summary>
public sealed class SystemLogCollector : ISystemLogCollector, IDisposable
{
    /// <summary>文件日志保留天数。</summary>
    private const int RetainedDays = 7;

    /// <summary>单文件最大行数（含分隔线；约 2000 条日志/天）。</summary>
    private const int MaxLines = 4000;

    /// <summary>共用置顶写入器（时间倒序 + 分隔线 + 行数封顶 + 过期清理）。</summary>
    private readonly DeviceInterface.DeviceLogFileWriter _writer;

    /// <summary>日志表封顶条数。</summary>
    private const int MaxEntries = 500;

    private readonly object _gate = new();
    private readonly List<SystemLogEntry> _entries = new();

    public SystemLogCollector()
    {
        _writer = new DeviceInterface.DeviceLogFileWriter(
            Path.Combine(AppContext.BaseDirectory, "logs", "system"),
            filePrefix: "system",
            retainedDays: RetainedDays,
            maxLines: MaxLines);
    }

    /// <inheritdoc />
    public event Action? EntryLogged;

    /// <inheritdoc />
    public void Log(string category, string message, string level = "Info")
    {
        lock (_gate)
        {
            _entries.Add(new SystemLogEntry
            {
                Time = DateTime.Now,
                Category = category,
                Message = message,
                Level = level,
            });
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(0);
            }
        }

        // 文件留痕（时间毫秒级，参照 PLC 通信日志格式）
        _writer.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] [{level}] {message}");

        // 锁外触发写入通知（订阅方自行取快照；实时面板刷新用）
        EntryLogged?.Invoke();
    }

    /// <summary>无需释放资源（保留 IDisposable 与 PLC 通信日志实现对称）。</summary>
    public void Dispose()
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<SystemLogEntry> GetEntries()
    {
        lock (_gate)
        {
            // 倒序拷贝，最新在前，快照与内部表隔离
            return _entries.AsEnumerable().Reverse().ToList();
        }
    }
}
