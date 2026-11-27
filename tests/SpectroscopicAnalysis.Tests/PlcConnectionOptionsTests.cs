using Microsoft.Extensions.Configuration;
using SpectroscopicAnalysis.Host.DeviceInterface;
using SpectroscopicAnalysis.Host.LoggingAlarm;

namespace SpectroscopicAnalysis.Tests;

/// <summary>
/// PLC 连接配置封装用例：默认值合法、越界快速失败、appsettings "Plc" 节 Bind 映射正确。
/// </summary>
public class PlcConnectionOptionsTests
{
    [Fact]
    public void 默认值_校验通过()
    {
        var options = new PlcConnectionOptions();
        options.Validate();
    }

    [Fact]
    public void 默认值_为ModbusTCP常规取值()
    {
        var options = new PlcConnectionOptions();
        Assert.Equal("192.168.0.1", options.Host);
        Assert.Equal(502, options.Port);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void 端口越界_校验抛出(int port)
    {
        var options = new PlcConnectionOptions { Port = port };
        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(((int)ErrorCodes.PlcConnectionConfigInvalid).ToString(), ex.Message);
    }

    [Fact]
    public void Host为空_校验抛出()
    {
        var options = new PlcConnectionOptions { Host = "  " };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public void 超时或重试越界_校验抛出(int connectTimeoutMs, int readWriteTimeoutMs, int retryCount)
    {
        var options = new PlcConnectionOptions
        {
            ConnectTimeoutMs = connectTimeoutMs,
            ReadWriteTimeoutMs = readWriteTimeoutMs,
            RetryCount = retryCount,
        };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Plc配置节_Bind映射正确()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Plc:Host"] = "10.0.0.8",
                ["Plc:Port"] = "1502",
                ["Plc:ConnectTimeoutMs"] = "5000",
                ["Plc:ReadWriteTimeoutMs"] = "3000",
                ["Plc:RetryCount"] = "2",
                ["Plc:ReconnectIntervalMs"] = "8000",
            })
            .Build();
        var options = new PlcConnectionOptions();
        config.GetSection("Plc").Bind(options);

        Assert.Equal("10.0.0.8", options.Host);
        Assert.Equal(1502, options.Port);
        Assert.Equal(5000, options.ConnectTimeoutMs);
        Assert.Equal(3000, options.ReadWriteTimeoutMs);
        Assert.Equal(2, options.RetryCount);
        Assert.Equal(8000, options.ReconnectIntervalMs);
        options.Validate();
    }
}
