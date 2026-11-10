namespace SpectroscopicAnalysis.Host.LoggingAlarm;

/// <summary>
/// 错误收集抽象（开发规范 11.2 错误链路第一环：业务 catch → 结构化日志 → 报警）。
/// 实现走 Serilog；VM 仅依赖本接口（MVVM 分层，禁止引用日志框架类型）。
/// </summary>
public interface IErrorCollector
{
    /// <summary>
    /// 记录业务错误：错误码必填，样品/任务上下文可选，结构化写入 Serilog 并供后续报警面板/统计消费。
    /// </summary>
    void Collect(int errorCode, string message, string? sampleId = null, Exception? exception = null);
}
