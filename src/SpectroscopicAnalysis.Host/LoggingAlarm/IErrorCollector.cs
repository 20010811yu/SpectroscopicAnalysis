namespace SpectroscopicAnalysis.Host.LoggingAlarm;

/// <summary>
/// 错误收集抽象（开发规范 11.2 错误链路第一环：业务 catch → 结构化日志 → 报警）。
/// 实现走 Serilog；VM 仅依赖本接口（MVVM 分层，禁止引用日志框架类型）。
/// </summary>
public interface IErrorCollector
{
    /// <summary>
    /// 记录业务错误：错误码必填，样品/任务上下文可选，结构化写入 Serilog 并追加内存报警表供报警面板/统计消费。
    /// </summary>
    void Collect(int errorCode, string message, string? sampleId = null, Exception? exception = null);

    /// <summary>未确认报警数（主控台 KPI「未确认报警」数据源；进程重启后清零）。</summary>
    int UnacknowledgedCount { get; }

    /// <summary>报警记录快照（最新在前，封顶保留；后续 D9 报警面板数据源）。</summary>
    IReadOnlyList<AlarmRecord> GetAlarms();

    /// <summary>确认单条报警。</summary>
    void Acknowledge(long alarmId);

    /// <summary>确认全部报警。</summary>
    void AcknowledgeAll();

    /// <summary>新报警产生事件（Collect 触发，任意线程；订阅方自行切回 UI 线程刷新报警面板）。
    /// 面板即时刷新数据源——避免依赖秒级定时轮询导致报警延迟出现。</summary>
    event Action? AlarmRaised;
}
