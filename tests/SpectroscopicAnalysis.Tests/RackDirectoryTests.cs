using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.SampleManager;
using Xunit;

namespace SpectroscopicAnalysis.Tests;

/// <summary>RackDirectory 可用架位查询测试（注册下拉选择数据源）。</summary>
public sealed class RackDirectoryTests
{
    [Fact]
    public void AllPositions_布局为5行5列共25个架位_按行优先排序()
    {
        Assert.Equal(25, RackDirectory.AllPositions.Count);
        Assert.Equal("A01", RackDirectory.AllPositions[0]);
        Assert.Equal("A05", RackDirectory.AllPositions[4]);
        Assert.Equal("B01", RackDirectory.AllPositions[5]);
        Assert.Equal("E05", RackDirectory.AllPositions[^1]);
    }

    [Fact]
    public void AvailablePositions_空库_返回全部25个空闲架位()
    {
        var available = RackDirectory.AvailablePositions([], []);

        Assert.Equal(25, available.Count);
        Assert.Equal(RackDirectory.AllPositions, available);
    }

    [Fact]
    public void AvailablePositions_标样占用_剔除对应架位()
    {
        var sct = new[] { new SctSample { SampleId = "SUS-001", RackPosition = "A01" } };

        var available = RackDirectory.AvailablePositions(sct, []);

        Assert.Equal(24, available.Count);
        Assert.DoesNotContain("A01", available);
    }

    [Fact]
    public void AvailablePositions_生产样占用_同样剔除()
    {
        var production = new[] { new ProductionSample { SampleId = "PL-001", RackPosition = "C04" } };

        var available = RackDirectory.AvailablePositions([], production);

        Assert.Equal(24, available.Count);
        Assert.DoesNotContain("C04", available);
    }

    [Fact]
    public void AvailablePositions_占用带空白_归一后仍剔除()
    {
        var sct = new[] { new SctSample { SampleId = "SUS-001", RackPosition = " B02 " } };

        var available = RackDirectory.AvailablePositions(sct, []);

        Assert.DoesNotContain("B02", available);
    }

    [Fact]
    public void AvailablePositions_空架位串_不视为占用()
    {
        var sct = new[] { new SctSample { SampleId = "SUS-001", RackPosition = null },
                           new SctSample { SampleId = "SUS-002", RackPosition = "" } };

        var available = RackDirectory.AvailablePositions(sct, []);

        Assert.Equal(25, available.Count);
    }

    [Fact]
    public void AvailablePositions_范围外架位码_不影响布局内可用清单()
    {
        var sct = new[] { new SctSample { SampleId = "SUS-001", RackPosition = "A12" } };

        var available = RackDirectory.AvailablePositions(sct, []);

        // A12 不在布局内：布局 25 位不受影响，仍全部可用
        Assert.Equal(25, available.Count);
    }

    // ==================== 生产样独立编号空间（P01–P25） ====================

    [Fact]
    public void ProductionAvailablePositions_空库返回全部25位且P编号()
    {
        var available = RackDirectory.ProductionAvailablePositions([]);

        Assert.Equal(25, available.Count);
        Assert.Equal("P01", available[0]);
        Assert.Equal("P05", available[4]);
        Assert.Equal("P25", available[^1]);
        Assert.DoesNotContain(available, c => c.StartsWith("A"));   // 与 SCT 编号空间独立
    }

    [Fact]
    public void ProductionAvailablePositions_生产样占用剔除且不受SCT占用影响()
    {
        var prod = new[] { new ProductionSample { SampleId = "P-1", RackPosition = "P01" },
                           new ProductionSample { SampleId = "P-2", RackPosition = " P07 " } };   // 带空白归一剔除
        var sct = new[] { new SctSample { SampleId = "SUS-001", RackPosition = "A01" } };

        var available = RackDirectory.ProductionAvailablePositions(prod);

        Assert.Equal(23, available.Count);
        Assert.DoesNotContain("P01", available);
        Assert.DoesNotContain("P07", available);
    }

    [Fact]
    public void ProductionAvailablePositions_SCT占用不影响生产样空间()
    {
        // SCT 样占满 A01–E05 不影响生产样 P 编号可用清单（修复共用编号导致下拉为空的问题）
        var sct = RackDirectory.AllPositions.Select((code, i) => new SctSample { SampleId = $"S-{i:00}", RackPosition = code });

        var available = RackDirectory.ProductionAvailablePositions([]);

        Assert.Equal(25, available.Count);
    }
}
