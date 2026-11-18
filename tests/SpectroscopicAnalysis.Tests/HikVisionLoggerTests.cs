using SpectroscopicAnalysis.Host.Vision;

namespace SpectroscopicAnalysis.Tests;

/// <summary>海康VM日志冒烟测试：构造并写入不抛异常（文件写入 bin\logs\hikvm\，按天滚动保留 3 天）。</summary>
public sealed class HikVisionLoggerTests
{
    [Fact]
    public void 构造并写入日志_不抛异常()
    {
        using var logger = new HikVisionLogger();

        logger.Log(HikVisionLogLevel.Info, "冒烟测试：海康VM日志通道可用");
        logger.Log(HikVisionLogLevel.Error, "冒烟测试：错误级别写入");

        // Serilog File sink 异步写且吞写异常，此处仅验证调用路径无异常
    }
}
