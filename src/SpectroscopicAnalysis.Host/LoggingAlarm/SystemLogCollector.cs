namespace SpectroscopicAnalysis.Host.LoggingAlarm;

/// <summary>
/// 全流程系统日志实现：内存环形表（封顶淘汰最旧，防长期运行内存膨胀；进程重启清零）。
/// 线程安全：Log 可能在任意线程（后台 I/O、托管服务）触发。
/// </summary>
public sealed class SystemLogCollector : ISystemLogCollector
{
    /// <summary>日志表封顶条数。</summary>
    private const int MaxEntries = 500;

    private readonly object _gate = new();
    private readonly List<SystemLogEntry> _entries = new();

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

        // 锁外触发写入通知（订阅方自行取快照；实时面板刷新用）
        EntryLogged?.Invoke();
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
