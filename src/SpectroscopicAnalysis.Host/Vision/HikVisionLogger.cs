using System.IO;
using Serilog;

namespace SpectroscopicAnalysis.Host.Vision;

/// <summary>海康 VM（海康威视机器视觉）日志级别常量。</summary>
public static class HikVisionLogLevel
{
    public const string Info = "INFO";
    public const string Warn = "WARN";
    public const string Error = "ERROR";
}

/// <summary>
/// 海康 VM 专用日志抽象（相机图像处理/表面识别/激发点规划/测高等相关内容留痕，任务清单第七节）。
/// 独立文件夹存放（logs\hikvm\），与主日志/PLC 通信日志分离；后续海康集成代码经此埋点。
/// </summary>
public interface IHikVisionLogger
{
    /// <summary>记录一条海康 VM 日志（level 用 <see cref="HikVisionLogLevel"/> 常量）。</summary>
    void Log(string level, string content);
}

/// <summary>
/// 海康 VM 日志实现：独立 Serilog 文件 logs\hikvm\hikvm-YYYYMMDD.log
/// （按天滚动、只保留最近 3 天，超期自动删除），UTF-8 中文可读；
/// 输出格式：时间(毫秒) [级别] 内容；不污染主日志与 PLC 日志配置。
/// </summary>
public sealed class HikVisionLogger : IHikVisionLogger, IDisposable
{
    /// <summary>日志保留天数（与 PLC 通信日志一致，只保留最近 3 天）。</summary>
    public const int RetainedDays = 3;

    /// <summary>专用 Serilog Logger（独立管道）。</summary>
    private readonly ILogger _logger;

    public HikVisionLogger()
    {
        _logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(AppContext.BaseDirectory, "logs", "hikvm", "hikvm-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedDays,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level}] {Content}{NewLine}",
                encoding: System.Text.Encoding.UTF8)
            .CreateLogger();
    }

    /// <inheritdoc />
    public void Log(string level, string content)
    {
        // 结构化属性经消息模板注入输出模板（时间戳由 Serilog 自动记录）
        _logger.Information("海康VM {Level} {Content}", level, content);
    }

    /// <summary>宿主退出时释放文件句柄并 flush。</summary>
    public void Dispose() => ((IDisposable)_logger).Dispose();
}
