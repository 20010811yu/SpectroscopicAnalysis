using System.IO;
using System.Text;

namespace SpectroscopicAnalysis.Host.DeviceInterface;

/// <summary>
/// PLC 通信交互地址数据表（单一来源：寄存器地址常量 + 各地址收发方向与数据内容说明）。
/// 仅描述地址与内容语义，<b>不含任何具体数据值</b>。
/// 写入时机：<b>不在本阶段调用</b>——当前 Stub 网关未连真实 PLC；待 Modbus 真实现与实际 PLC
/// 连接成功后，由网关的连接成功回调调用 <see cref="WriteCsv"/>，在 PLC 日志目录
/// （logs\plc\）自动生成/更新「地址数据表.csv」（UTF-8 带 BOM，Excel 直接可读）。
/// </summary>
public static class PlcAddressTable
{
    /// <summary>逻辑寄存器地址（Stub 阶段占位名；Modbus 真实现后在此替换为真实寄存器地址，网关与表格自动同步）。</summary>
    public const string AddrCommand = "命令寄存器 4xxxx";
    public const string AddrStatus = "状态寄存器 4xxxx";
    public const string AddrSafety = "安全寄存器 4xxxx";
    public const string AddrHeartbeat = "心跳寄存器 4xxxx";

    /// <summary>生成目标文件名（位于 PLC 日志目录 logs\plc\ 下）。</summary>
    private const string FileName = "地址数据表.csv";

    /// <summary>
    /// 地址交互条目注册表：每个交互点的地址、方向（发送/接收）、数据内容说明。
    /// 上位机→PLC 写为「发送」，PLC→上位机 读为「接收」。
    /// </summary>
    public static IReadOnlyList<(string Address, string Direction, string Description)> Entries { get; } =
    [
        (AddrCommand, PlcCommDirection.Send, "循环任务下发：命令字(1生产/2标准化/3检查样)+样品编号+抓取高度"),
        (AddrCommand, PlcCommDirection.Send, "测高指令：命令字+样品编号"),
        (AddrStatus, PlcCommDirection.Receive, "任务状态轮询：任务状态/样品编号/测得厚度"),
        (AddrSafety, PlcCommDirection.Receive, "预检信号：SafetyOK/RobotReady/SparkStandReady/样品位置一致"),
        (AddrHeartbeat, PlcCommDirection.Send, "心跳递增计数（1–2 秒节拍）"),
    ];

    /// <summary>
    /// 在 PLC 日志目录下生成/覆盖地址数据表 CSV。由 Modbus 真实现网关在连接成功时调用，
    /// 表格内容随地址常量更新而自动同步。
    /// </summary>
    /// <param name="plcLogDir">PLC 日志目录（logs\plc\），不存在则自动创建。</param>
    public static void WriteCsv(string plcLogDir)
    {
        Directory.CreateDirectory(plcLogDir);
        var sb = new StringBuilder();
        sb.AppendLine("PLC地址,方向,数据内容说明");
        foreach (var entry in Entries)
        {
            sb.Append(entry.Address).Append(',')
              .Append(entry.Direction).Append(',')
              .AppendLine(entry.Description);
        }
        // UTF-8 带 BOM：保证 Excel 直接打开中文不乱码
        File.WriteAllText(Path.Combine(plcLogDir, FileName), sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
