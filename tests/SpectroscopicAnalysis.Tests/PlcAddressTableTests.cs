using System.IO;
using SpectroscopicAnalysis.Host.DeviceInterface;
using Xunit;

namespace SpectroscopicAnalysis.Tests;

/// <summary>
/// PLC 地址数据表测试：CSV 生成（表头/行数/不含具体数据值）、注册表与网关地址常量一致性。
/// </summary>
public class PlcAddressTableTests
{
    [Fact]
    public void WriteCsv_生成表头与全部条目行()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plc-addr-table-tests", Guid.NewGuid().ToString("N"));
        try
        {
            PlcAddressTable.WriteCsv(dir);

            var path = Path.Combine(dir, "地址数据表.csv");
            Assert.True(File.Exists(path));
            var lines = File.ReadAllLines(path);
            // 表头 + 5 个交互点（命令×2 / 状态 / 安全 / 心跳）
            Assert.Equal("PLC地址,方向,数据内容说明", lines[0]);
            Assert.Equal(PlcAddressTable.Entries.Count + 1, lines.Length);
            // 每条数据行均为三列：地址,方向(发送/接收),说明
            Assert.All(lines[1..], l =>
            {
                var cols = l.Split(',');
                Assert.Equal(3, cols.Length);
                Assert.Contains(cols[1], new[] { PlcCommDirection.Send, PlcCommDirection.Receive });
            });
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Entries_只含地址方向与说明_不含具体数据值()
    {
        Assert.All(PlcAddressTable.Entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Address));
            Assert.Contains(e.Direction, new[] { PlcCommDirection.Send, PlcCommDirection.Receive });
            Assert.False(string.IsNullOrWhiteSpace(e.Description));
            // 等号赋值是具体数据值的特征（表格只描述语义，不落数据）
            Assert.DoesNotContain("=", e.Description);
        });
    }

    [Fact]
    public void Entries_地址与网关使用的四个寄存器一致()
    {
        var addresses = Entries().Select(e => e.Address).ToHashSet();
        Assert.Equal(new[]
        {
            PlcAddressTable.AddrCommand, PlcAddressTable.AddrStatus,
            PlcAddressTable.AddrSafety, PlcAddressTable.AddrHeartbeat,
        }.ToHashSet(), addresses);
        // 网关日志埋点引用的常量必须来自同一单一来源
        Assert.Equal("命令寄存器 4xxxx", PlcAddressTable.AddrCommand);
    }

    private static IReadOnlyList<(string Address, string Direction, string Description)> Entries() =>
        PlcAddressTable.Entries;
}
