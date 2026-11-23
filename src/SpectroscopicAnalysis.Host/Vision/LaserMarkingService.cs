using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SpectroscopicAnalysis.Host.LoggingAlarm;

namespace SpectroscopicAnalysis.Host.Vision;

/// <summary>
/// 激光打标服务实现：从配置路径（默认 D:\Desktop\2.txt）读取打标文本文件内容。
/// 读取失败走统一错误链（9003 段登记复用：外部打标软件相关失败），返回空串不抛出（页面展示为空由用户感知）。
/// </summary>
public class LaserMarkingService : ILaserMarkingService
{
    private readonly string _contentFilePath;
    private readonly string _textEzdFilePath;
    private readonly string _barcodeEzdFilePath;
    private readonly IEzCad2Controller _ezCad2Controller;
    private readonly ILogger<LaserMarkingService> _logger;
    private readonly IErrorCollector _errorCollector;
    private readonly LoggingAlarm.ISystemLogCollector _systemLog;

    public LaserMarkingService(IConfiguration configuration, IEzCad2Controller ezCad2Controller, ILogger<LaserMarkingService> logger, IErrorCollector errorCollector, LoggingAlarm.ISystemLogCollector systemLog)
    {
        _systemLog = systemLog;
        _contentFilePath = configuration["LaserMarking:ContentFilePath"] ?? @"D:\Desktop\2.txt";
        // 文本打印用 EzCad2:EzdFilePath（启动默认工程）；条码打印为独立配置
        _textEzdFilePath = configuration["EzCad2:EzdFilePath"] ?? @"D:\Desktop\261117\2.ezd";
        _barcodeEzdFilePath = configuration["LaserMarking:BarcodeEzdFilePath"] ?? @"D:\Desktop\261117\1.ezd";
        _ezCad2Controller = ezCad2Controller;
        _logger = logger;
        _errorCollector = errorCollector;
        // 全流程日志：EzCad2 上下线状态变化留痕（此前仅掉线报警，上线/恢复无记录）
        _ezCad2Controller.OnlineStateChanged += () =>
            _systemLog.Log("激光打标",
                _ezCad2Controller.IsOnline ? "打标机 EzCad2 上线" : "打标机 EzCad2 离线",
                _ezCad2Controller.IsOnline ? "Info" : "Warning");
    }

    /// <inheritdoc/>
    public string ReadMarkingContent()
    {
        try
        {
            if (!File.Exists(_contentFilePath))
            {
                _logger.LogWarning("打标文本文件不存在：{ContentFilePath}", _contentFilePath);
                return string.Empty;
            }

            return File.ReadAllText(_contentFilePath);
        }
        catch (Exception ex)
        {
            // 携带登记错误码走统一错误链（9003），不吞异常；页面侧返回空串兜底
            _errorCollector.Collect(ErrorCodes.EzCad2LaunchFailed, $"读取打标文本文件失败：{_contentFilePath}", exception: ex);
            return string.Empty;
        }
    }

    /// <inheritdoc/>
    public bool WriteMarkingContent(string content)
    {
        try
        {
            // 原子写入：先写临时文件再替换，避免写一半被 EzCad2 读取到残缺内容
            var tempPath = _contentFilePath + ".tmp";
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, _contentFilePath, overwrite: true);
            _logger.LogInformation("打标文本已写回：{ContentFilePath}", _contentFilePath);
            return true;
        }
        catch (Exception ex)
        {
            // 携带登记错误码走统一错误链（9003），不吞异常；返回失败由页面提示
            _errorCollector.Collect(ErrorCodes.EzCad2LaunchFailed, $"写回打标文本文件失败：{_contentFilePath}", exception: ex);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> SwitchMarkingModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        // 模式 → 工程文件映射：文本打印=2.ezd（启动默认），条码打印=1.ezd
        var ezdPath = mode switch
        {
            "文本打印" => _textEzdFilePath,
            "条码打印" => _barcodeEzdFilePath,
            _ => null,
        };

        if (ezdPath is null)
        {
            return $"未知的打标方式：{mode}";
        }

        _logger.LogInformation("切换打标方式：{Mode}（工程文件 {EzdPath}）", mode, ezdPath);
        return await _ezCad2Controller.SwitchEzdFileAsync(ezdPath, cancellationToken);
    }

    /// <inheritdoc />
    public void LogPrintStarted(string content, string mode) =>
        _systemLog.Log("激光打标", $"{content}（{mode}）打印开始");

    /// <inheritdoc />
    public void LogPrintFinished(bool success, string? failReason = null)
    {
        _systemLog.Log("激光打标", success ? "打印结束：完成" : "打印结束：失败", success ? "Info" : "Failed");
        if (!success)
        {
            _systemLog.Log("激光打标", $"失败原因：{failReason ?? "未知"}", "Failed");
        }
    }

    /// <inheritdoc />
    public void LogModeChanged(string oldMode, string newMode, bool success, string? error = null)
    {
        _systemLog.Log("激光打标", $"打印方式变更：{oldMode} → {newMode}", success ? "Info" : "Failed");
        if (!success)
        {
            _systemLog.Log("激光打标", $"变更失败原因：{error ?? "未知"}", "Failed");
        }
    }

    /// <inheritdoc />
    public void LogContentChanged(string oldContent, string newContent) =>
        _systemLog.Log("激光打标", $"打印内容变更：{oldContent} → {newContent}");
}