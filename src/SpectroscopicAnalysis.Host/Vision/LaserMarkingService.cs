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
    private readonly ILogger<LaserMarkingService> _logger;
    private readonly IErrorCollector _errorCollector;

    public LaserMarkingService(IConfiguration configuration, ILogger<LaserMarkingService> logger, IErrorCollector errorCollector)
    {
        _contentFilePath = configuration["LaserMarking:ContentFilePath"] ?? @"D:\Desktop\2.txt";
        _logger = logger;
        _errorCollector = errorCollector;
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
}
