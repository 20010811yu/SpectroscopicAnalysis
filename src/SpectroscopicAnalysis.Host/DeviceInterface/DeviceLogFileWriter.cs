using System.IO;

namespace SpectroscopicAnalysis.Host.DeviceInterface;

/// <summary>设备日志共用保留策略（PLC 通信日志与海康 VM 日志一致）。</summary>
public static class DeviceLogFileWriterDefaults
{
    /// <summary>日志保留天数（只保留最近 3 天）。</summary>
    public const int RetainedDays = 3;

    /// <summary>单文件最大行数（含分隔行；超出从尾部截断）。</summary>
    public const int MaxLines = 2000;
}

/// <summary>
/// 设备日志文件写入器（PLC 通信日志与海康 VM 日志共用）：
/// 条目置顶写入（时间倒序，最新在最前）、条目之间明显分隔线、
/// 行数封顶截断防膨胀、按天文件名 + 过期文件清理（保留最近 N 天）。
/// I/O 失败静默忽略——日志基础设施不应打断主流程。
/// </summary>
public sealed class DeviceLogFileWriter
{
    /// <summary>条目之间的分隔线（明显横线）。</summary>
    public const string Separator = "──────────────────────────────────────────";

    /// <summary>保护读改写的锁（多线程埋点串行化）。</summary>
    private readonly object _gate = new();

    private readonly string _directory;
    private readonly string _filePrefix;
    private readonly int _retainedDays;
    private readonly int _maxLines;

    /// <param name="directory">日志目录（不存在自动创建）。</param>
    /// <param name="filePrefix">按天文件名前缀（如 plc → plc-20261118.log）。</param>
    /// <param name="retainedDays">过期文件保留天数（含当天外最多 N-1 天历史）。</param>
    /// <param name="maxLines">单文件最大行数（含分隔行；超出从尾部截断）。</param>
    public DeviceLogFileWriter(string directory, string filePrefix, int retainedDays, int maxLines)
    {
        _directory = directory;
        _filePrefix = filePrefix;
        _retainedDays = retainedDays;
        _maxLines = maxLines;
    }

    /// <summary>写入一条日志：新条目置顶，条目之间插入分隔线（时间倒序排列）。</summary>
    public void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"{_filePrefix}-{DateTime.Now:yyyyMMdd}.log");

                // 读旧内容（新条目插最前 → 时间倒序；条目之间补分隔线）
                var existing = File.Exists(path) ? File.ReadAllLines(path) : [];
                var lines = new List<string>(existing.Length + 2) { line };
                if (existing.Length > 0)
                {
                    lines.Add(Separator);
                    lines.AddRange(existing);
                }

                // 行数封顶：从尾部截断（保留的都是最新内容）
                if (lines.Count > _maxLines)
                {
                    lines = lines.Take(_maxLines).ToList();
                }

                File.WriteAllLines(path, lines);

                CleanupExpiredFiles();
            }
        }
        catch (IOException)
        {
            // 日志写失败不影响业务（文件占用/磁盘异常等）
        }
        catch (UnauthorizedAccessException)
        {
            // 目录无写权限等，静默忽略
        }
    }

    /// <summary>清理同前缀的过期文件（按文件名日期判断，保留最近 N 天）。</summary>
    private void CleanupExpiredFiles()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_directory, $"{_filePrefix}-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file); // prefix-yyyyMMdd
                var datePart = name.Length >= 8 ? name[^8..] : string.Empty;
                if (DateTime.TryParseExact(datePart, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var day) &&
                    day < DateTime.Today.AddDays(-(_retainedDays - 1)))
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
            // 清理失败不影响业务（文件被占用等，下次写入再试）
        }
        catch (UnauthorizedAccessException)
        {
            // 删除权限不足，静默忽略
        }
    }
}
