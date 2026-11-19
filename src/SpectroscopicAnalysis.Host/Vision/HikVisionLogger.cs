using System.IO;

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
/// 海康 VM 日志实现：独立文件 logs\hikvm\hikvm-YYYYMMDD.log（按天文件名、只保留最近 3 天）。
/// 条目格式：[时间(毫秒)] [级别] 内容；新条目置顶（时间倒序）、条目之间明显分隔线；
/// 不污染主日志与 PLC 日志。
/// </summary>
public sealed class HikVisionLogger : IHikVisionLogger, IDisposable
{
    /// <summary>共用置顶写入器（时间倒序 + 分隔线 + 行数封顶 + 过期清理）。</summary>
    private readonly DeviceInterface.DeviceLogFileWriter _writer;

    /// <summary>全流程系统日志（海康 VM 事件同步进主控台「运行日志」面板，类目「海康VM」）。</summary>
    private readonly LoggingAlarm.ISystemLogCollector _systemLog;

    public HikVisionLogger(LoggingAlarm.ISystemLogCollector systemLog)
    {
        _systemLog = systemLog;
        _writer = new DeviceInterface.DeviceLogFileWriter(
            Path.Combine(AppContext.BaseDirectory, "logs", "hikvm"),
            filePrefix: "hikvm",
            retainedDays: DeviceInterface.DeviceLogFileWriterDefaults.RetainedDays,
            maxLines: DeviceInterface.DeviceLogFileWriterDefaults.MaxLines);
    }

    /// <inheritdoc />
    public void Log(string level, string content)
    {
        _writer.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {content}");
        // 同步进全流程日志：VM 方案加载/图像处理等海康视觉事件在主控台可见（级别映射着色）
        var sysLevel = level switch
        {
            HikVisionLogLevel.Warn => "Warning",
            HikVisionLogLevel.Error => "Failed",
            _ => "Info",
        };
        _systemLog.Log("海康VM", content, sysLevel);
    }

    /// <summary>无需释放资源（保留 IDisposable 与旧实现兼容）。</summary>
    public void Dispose()
    {
    }
}
