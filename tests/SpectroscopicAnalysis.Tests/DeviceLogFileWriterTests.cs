using System.IO;
using SpectroscopicAnalysis.Host.DeviceInterface;

namespace SpectroscopicAnalysis.Tests;

/// <summary>设备日志写入器测试：条目置顶（时间倒序）/分隔线/行数封顶/过期文件清理。</summary>
public sealed class DeviceLogFileWriterTests
{
    /// <summary>临时目录（每个测试独立，结束后清理）。</summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"devlog-{Guid.NewGuid():N}");

    private DeviceLogFileWriter Create(int retainedDays = 3, int maxLines = 2000) =>
        new(_dir, filePrefix: "plc", retainedDays: retainedDays, maxLines: maxLines);

    private string TodayFile => Path.Combine(_dir, $"plc-{DateTime.Now:yyyyMMdd}.log");

    [Fact]
    public void 写入多条_新条目置顶且条目间有分隔线()
    {
        var writer = Create();

        writer.Write("[10:00:01.000] [发送] 命令寄存器: A");
        writer.Write("[10:00:02.000] [接收] 状态寄存器: B");
        writer.Write("[10:00:03.000] [发送] 心跳寄存器: C");

        var lines = File.ReadAllLines(TodayFile);
        // 倒序：最新在最前；条目间有明显分隔线
        Assert.StartsWith("[10:00:03.000]", lines[0]);
        Assert.Equal(DeviceLogFileWriter.Separator, lines[1]);
        Assert.StartsWith("[10:00:02.000]", lines[2]);
        Assert.Equal(DeviceLogFileWriter.Separator, lines[3]);
        Assert.StartsWith("[10:00:01.000]", lines[4]);
    }

    [Fact]
    public void 行数封顶_超出从尾部截断()
    {
        var writer = Create(maxLines: 11);

        for (var i = 1; i <= 8; i++)
        {
            writer.Write($"条目{i:00}");
        }

        var lines = File.ReadAllLines(TodayFile);
        Assert.True(lines.Length <= 11);
        Assert.StartsWith("条目08", lines[0]);              // 最新保留
        Assert.DoesNotContain("条目01", lines);             // 最旧被截断
        Assert.DoesNotContain("条目02", lines);
        Assert.Contains("条目03", lines);                   // 截断边界：剩余最旧条目
    }

    [Fact]
    public void 过期文件_写入时自动清理()
    {
        Directory.CreateDirectory(_dir);
        var oldDate = DateTime.Today.AddDays(-5).ToString("yyyyMMdd");
        var oldFile = Path.Combine(_dir, $"plc-{oldDate}.log");
        File.WriteAllText(oldFile, "过期内容");

        Create(retainedDays: 3).Write("新条目");

        Assert.False(File.Exists(oldFile));                 // 超过保留 3 天的文件被删除
        Assert.True(File.Exists(TodayFile));
    }

    [Fact]
    public void 未过期文件_不被清理()
    {
        Directory.CreateDirectory(_dir);
        var keepDate = DateTime.Today.AddDays(-1).ToString("yyyyMMdd");
        var keepFile = Path.Combine(_dir, $"plc-{keepDate}.log");
        File.WriteAllText(keepFile, "昨日日志");

        Create(retainedDays: 3).Write("新条目");

        Assert.True(File.Exists(keepFile));                 // 3 天保留期内不删
    }

    [Fact]
    public void 目录不存在_自动创建()
    {
        Assert.False(Directory.Exists(_dir));

        Create().Write("首条");

        Assert.True(Directory.Exists(_dir));
        Assert.True(File.Exists(TodayFile));
    }
}
