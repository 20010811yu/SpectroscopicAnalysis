using Serilog;

namespace SpectroscopicAnalysis.Host.LoggingAlarm;

/// <summary>
/// 错误收集实现：Serilog 结构化日志（错误码/样品上下文/异常堆栈必含，开发规范 11.2）
/// + 内存报警表（未确认计数/查询/确认，主控台 KPI 与后续 D9 报警面板数据源；进程重启清零）。
/// </summary>
public sealed class ErrorCollector : IErrorCollector
{
    /// <summary>内存报警表封顶条数（超过后淘汰最旧；防长期运行内存膨胀）。</summary>
    private const int MaxAlarms = 500;

    private readonly ISystemLogCollector _systemLog;

    /// <summary>报警表自增标识。</summary>
    private long _alarmIdSeed;

    /// <summary>保护报警表的锁（Collect 可能在任意线程触发）。</summary>
    private readonly object _gate = new();

    /// <summary>报警记录（追加序；快照时倒序输出最新在前）。</summary>
    private readonly List<AlarmRecord> _alarms = new();

    public ErrorCollector(ISystemLogCollector systemLog)
    {
        _systemLog = systemLog;
    }

    public void Collect(int errorCode, string message, string? sampleId = null, Exception? exception = null)
    {
        lock (_gate)
        {
            _alarms.Add(new AlarmRecord
            {
                AlarmId = ++_alarmIdSeed,
                Time = DateTime.Now,
                ErrorCode = errorCode,
                Message = message,
                SampleId = sampleId,
            });
            if (_alarms.Count > MaxAlarms)
            {
                _alarms.RemoveAt(0);
            }
        }

        // 报警同步写入全流程日志（报警类目，Failed 级别）——主控台「运行日志」面板联动
        _systemLog.Log("报警",
            string.IsNullOrEmpty(sampleId) ? $"{errorCode} {message}" : $"{errorCode} {message}（样品 {sampleId}）",
            "Failed");

        Log.ForContext("ErrorCode", errorCode)
            .ForContext("SampleID", sampleId ?? "-")
            .Error(exception, "业务错误 {Message}", message);

        // 通知订阅方（报警面板/KPI 即时刷新；任意线程触发，订阅方自行切 UI 线程）
        AlarmRaised?.Invoke();
    }

    /// <inheritdoc />
    public event Action? AlarmRaised;

    /// <summary>未确认报警数（主控台 KPI 数据源）。</summary>
    public int UnacknowledgedCount
    {
        get
        {
            lock (_gate)
            {
                return _alarms.Count(a => !a.IsAcknowledged);
            }
        }
    }

    /// <summary>报警记录快照（最新在前）。</summary>
    public IReadOnlyList<AlarmRecord> GetAlarms()
    {
        lock (_gate)
        {
            // 倒序拷贝，快照与内部表隔离
            return _alarms.AsEnumerable().Reverse().ToList();
        }
    }

    /// <summary>确认单条报警（不存在时静默忽略）。</summary>
    public void Acknowledge(long alarmId)
    {
        lock (_gate)
        {
            var alarm = _alarms.FirstOrDefault(a => a.AlarmId == alarmId);
            if (alarm is not null)
            {
                alarm.IsAcknowledged = true;
            }
        }
    }

    /// <summary>全部确认。</summary>
    public void AcknowledgeAll()
    {
        lock (_gate)
        {
            foreach (var a in _alarms)
            {
                a.IsAcknowledged = true;
            }
        }
    }
}
