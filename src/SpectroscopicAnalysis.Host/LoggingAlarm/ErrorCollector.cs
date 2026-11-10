using Serilog;

namespace SpectroscopicAnalysis.Host.LoggingAlarm;

/// <summary>错误收集实现：Serilog 结构化日志（错误码/样品上下文/异常堆栈必含，开发规范 11.2）。</summary>
public sealed class ErrorCollector : IErrorCollector
{
    public void Collect(int errorCode, string message, string? sampleId = null, Exception? exception = null)
    {
        Log.ForContext("ErrorCode", errorCode)
            .ForContext("SampleID", sampleId ?? "-")
            .Error(exception, "业务错误 {Message}", message);
    }
}
