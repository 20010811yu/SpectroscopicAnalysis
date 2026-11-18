namespace SpectroscopicAnalysis.Host.DeviceInterface;

/// <summary>PLC 交互方向常量（通信日志方向字段）。</summary>
public static class PlcCommDirection
{
    /// <summary>上位机 → PLC。</summary>
    public const string Send = "发送";

    /// <summary>PLC → 上位机。</summary>
    public const string Receive = "接收";
}

/// <summary>
/// PLC 数据交互日志抽象（独立文件留痕；每条含 PLC 地址/时间/内容/发送或接收方向）。
/// 仅记录 PLC 交互（GNR/铣床不经此通道）；VM 层不感知，设备网关埋点用。
/// </summary>
public interface IPlcCommLogger
{
    /// <summary>记录一条 PLC 交互（direction 用 <see cref="PlcCommDirection"/> 常量）。</summary>
    void Log(string direction, string address, string content);
}
