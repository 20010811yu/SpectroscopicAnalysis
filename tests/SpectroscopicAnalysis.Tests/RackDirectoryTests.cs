using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.SampleManager;
using Xunit;

namespace SpectroscopicAnalysis.Tests;

/// <summary>RackDirectory 可用架位查询测试（注册下拉选择数据源）。</summary>
public sealed class RackDirectoryTests
{
    [Fact]
    public void AllPositions_布局为5行8列共40个架位_按行优先排序()
    {
        Assert.Equal(40, RackDirectory.AllPositions.Count);
        Assert.Equal("A01", RackDirectory.AllPositions[0]);
        Assert.Equal("A08", RackDirectory.AllPositions[7]);
        Assert.Equal("B01", RackDirectory.AllPositions[8]);
        Assert.Equal("E08", RackDirectory.AllPositions[^1]);
    }

    [Fact]
    public void AvailablePositions_空库_返回全部40个空闲架位()
    {
        var available = RackDirectory.AvailablePositions([], []);

        Assert.Equal(40, available.Count);
        Assert.Equal(RackDirectory.AllPositions, available);
    }

    [Fact]
    public void AvailablePositions_标样占用_剔除对应架位()
    {
        var sct = new[] { new SctSample { SampleId = "SUS-001", RackPosition = "A01" } };

        var available = RackDirectory.AvailablePositions(sct, []);

        Assert.Equal(39, available.Count);
        Assert.DoesNotContain("A01", available);
    }

    [Fact]
    public void AvailablePositions_生产样占用_同样剔除()
    {
        var production = new[] { new ProductionSample { SampleId = "PL-001", RackPosition = "C04" } };

        var available = RackDirectory.AvailablePositions([], production);

        Assert.Equal(39, available.Count);
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

        Assert.Equal(40, available.Count);
    }

    [Fact]
    public void AvailablePositions_范围外架位码_不影响布局内可用清单()
    {
        var sct = new[] { new SctSample { SampleId = "SUS-001", RackPosition = "A12" } };

        var available = RackDirectory.AvailablePositions(sct, []);

        // A12 不在布局内：布局 40 位不受影响，仍全部可用
        Assert.Equal(40, available.Count);
    }
}
