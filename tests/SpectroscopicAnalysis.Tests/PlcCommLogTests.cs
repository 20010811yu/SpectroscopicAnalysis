using SpectroscopicAnalysis.Host.DeviceInterface;

namespace SpectroscopicAnalysis.Tests;

/// <summary>Fake PLC 通信日志：记录 (方向, 地址, 内容) 供断言，不写文件。</summary>
internal sealed class FakePlcCommLogger : IPlcCommLogger
{
    public List<(string Direction, string Address, string Content)> Entries { get; } = new();

    public void Log(string direction, string address, string content) =>
        Entries.Add((direction, address, content));
}

/// <summary>PLC 数据交互日志埋点测试：Stub 网关各 PLC 交互产生正确方向/地址/内容的日志；GNR 激发不埋点。</summary>
public sealed class PlcCommLogTests
{
    [Fact]
    public async Task 下发周期任务_记录发送与命令寄存器内容()
    {
        var log = new FakePlcCommLogger();
        var gateway = new StubDeviceGateway(log);

        await gateway.SendCycleStartAsync(idCycle: 1, "P-001", gripHeight: 2.5, CancellationToken.None);

        var entry = log.Entries.Single();
        Assert.Equal(PlcCommDirection.Send, entry.Direction);
        Assert.Contains("命令寄存器", entry.Address);
        Assert.Contains("命令字=1", entry.Content);
        Assert.Contains("样品=P-001", entry.Content);
        Assert.Contains("抓取高度=2.5", entry.Content);
    }

    [Fact]
    public async Task 测高命令_记录发送()
    {
        var log = new FakePlcCommLogger();
        var gateway = new StubDeviceGateway(log);

        await gateway.MeasureHeightAsync("P-002", CancellationToken.None);

        var entry = log.Entries.Single();
        Assert.Equal(PlcCommDirection.Send, entry.Direction);
        Assert.Contains("测高", entry.Content);
        Assert.Contains("P-002", entry.Content);
    }

    [Fact]
    public async Task 状态与安全轮询_记录接收与信号内容()
    {
        var log = new FakePlcCommLogger();
        var gateway = new StubDeviceGateway(log);

        await gateway.GetTaskStatusAsync(CancellationToken.None);
        await gateway.GetSafetyStatusAsync(CancellationToken.None);

        Assert.Equal(2, log.Entries.Count);
        Assert.All(log.Entries, e => Assert.Equal(PlcCommDirection.Receive, e.Direction));
        Assert.Contains(log.Entries, e => e.Address.Contains("状态寄存器") && e.Content.Contains("状态="));
        var safety = log.Entries.Single(e => e.Address.Contains("安全寄存器"));
        Assert.Contains("SafetyOK=True", safety.Content);
        Assert.Contains("位置一致=True", safety.Content);
    }

    [Fact]
    public void 心跳节拍_记录发送与递增计数()
    {
        var log = new FakePlcCommLogger();
        var gateway = new StubDeviceGateway(log);

        var c1 = gateway.TickHeartbeat();
        var c2 = gateway.TickHeartbeat();

        Assert.Equal(2, log.Entries.Count);
        Assert.All(log.Entries, e => Assert.Equal(PlcCommDirection.Send, e.Direction));
        Assert.Contains("心跳寄存器", log.Entries[0].Address);
        Assert.Contains($"计数值={c1}", log.Entries[0].Content);
        Assert.Contains($"计数值={c2}", log.Entries[1].Content);
    }

    [Fact]
    public void 心跳丢失_记录异常留痕()
    {
        var log = new FakePlcCommLogger();
        var gateway = new StubDeviceGateway(log);

        gateway.SimulateHeartbeatLost("连接超时");

        var entry = log.Entries.Single();
        Assert.Contains("心跳丢失", entry.Content);
        Assert.Contains("连接超时", entry.Content);
    }

    [Fact]
    public async Task GNR激发交互_不产生PLC通信日志()
    {
        var log = new FakePlcCommLogger();
        var gateway = new StubDeviceGateway(log);

        await gateway.TriggerSparkAsync("P-001", CancellationToken.None);
        await gateway.ReadResultAsync(CancellationToken.None);

        Assert.Empty(log.Entries);                          // 仅 PLC 专用，GNR 不埋点
    }

    [Fact]
    public async Task 未注入日志器_业务不受影响()
    {
        var gateway = new StubDeviceGateway();              // 默认无日志器

        await gateway.SendCycleStartAsync(1, "P-001", 0, CancellationToken.None);
        Assert.Single(gateway.SentCycles);                  // 不抛异常，业务正常
    }
}
