using SpectroscopicAnalysis.Host.LoggingAlarm;

namespace SpectroscopicAnalysis.Host.DeviceInterface;

/// <summary>
/// PLC 连接集中配置（AGENTS.md 规则 5：超时与重试上限值取集中配置）。
/// appsettings.json "Plc" 节绑定（Bind + 单例 POCO，与 WorkflowOptions 同模式）；
/// 仅含通用连接参数，不含 Modbus 细节——后续 Modbus 真实现（规则 2 可替换 OPC UA）以此作为构造注入，
/// 不进入 DeviceInterfaces.cs 设备抽象。
/// </summary>
public sealed class PlcConnectionOptions
{
    /// <summary>PLC（Modbus TCP 服务端）地址。</summary>
    public string Host { get; set; } = "192.168.0.1";

    /// <summary>PLC 端口（Modbus TCP 默认 502）。</summary>
    public int Port { get; set; } = 502;

    /// <summary>建立 TCP 连接超时（毫秒）。</summary>
    public int ConnectTimeoutMs { get; set; } = 3_000;

    /// <summary>单次读/写寄存器超时（毫秒）。</summary>
    public int ReadWriteTimeoutMs { get; set; } = 2_000;

    /// <summary>通信失败重试上限（耗尽走报警链路，不自动无限重试）。</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>连接中断后重连间隔（毫秒）。</summary>
    public int ReconnectIntervalMs { get; set; } = 5_000;

    /// <summary>
    /// 启动时快速失败校验：端口/超时/重试越界抛 <see cref="InvalidOperationException"/>
    /// （携带错误码 <see cref="ErrorCodes.PlcConnectionConfigInvalid"/>，"Plc" 前缀标明配置节上下文）。
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            Throw("Host 不能为空");
        if (Port is < 1 or > 65535)
            Throw($"Port 越界（{Port}，须 1–65535）");
        if (ConnectTimeoutMs <= 0)
            Throw($"ConnectTimeoutMs 须 >0（当前 {ConnectTimeoutMs}）");
        if (ReadWriteTimeoutMs <= 0)
            Throw($"ReadWriteTimeoutMs 须 >0（当前 {ReadWriteTimeoutMs}）");
        if (RetryCount < 1)
            Throw($"RetryCount 须 ≥1（当前 {RetryCount}）");
        if (ReconnectIntervalMs <= 0)
            Throw($"ReconnectIntervalMs 须 >0（当前 {ReconnectIntervalMs}）");

        void Throw(string detail) =>
            throw new InvalidOperationException(
                $"[{ErrorCodes.PlcConnectionConfigInvalid}] PLC 连接配置非法：{detail}（appsettings.json \"Plc\" 节）");
    }
}
