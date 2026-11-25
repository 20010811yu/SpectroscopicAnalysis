// modbus-offset-probe — Modbus TCP 地址偏移探针（纯标准库单文件，dotnet run --file 运行）
// 用途：对配置基准地址及候选偏移逐一读保持寄存器(0x03)，输出对照表以定位真实地址偏移。
// 用法示例：
//   dotnet run --file offset-probe.cs -- --host 192.168.0.1 --port 502 --unit 1 \
//     --start 4000 --count 4 --sweep 2 --offsets "-4,-2,-1,0,1,2,4"
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
using System.Globalization;
using System.Net.Sockets;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        Modbus TCP 地址偏移探针
        参数：
          --host <ip>        PLC 地址（必填）
          --port <int>       Modbus TCP 端口，默认 502
          --unit <int>       Unit ID，默认 1
          --start <int>      配置基准寄存器地址（协议 0 基地址；必填）
          --count <int>      每次读取寄存器个数，默认 4（上限 125）
          --sweep <int>      基准地址前后各扩展扫描的寄存器数，默认 0（不扩展）
          --offsets <list>   候选偏移列表，默认 "-4,-2,-1,0,1,2,4"
          --timeout <ms>     连接/读取超时，默认 3000
        """);
    return 0;
}

// —— 参数解析 —— //
int Opt(string name, int def) =>
    int.TryParse(args.Length > Array.IndexOf(args, name) + 1 ? args[Array.IndexOf(args, name) + 1] : null,
        out var v) ? v : def;

if (!args.Contains("--host") || !args.Contains("--start"))
{
    Console.Error.WriteLine("缺少 --host 或 --start 参数（--help 查看用法）");
    return 1;
}
var host = args[Array.IndexOf(args, "--host") + 1];
var port = Opt("--port", 502);
var unit = (byte)Opt("--unit", 1);
var start = Opt("--start", 0);
var count = Math.Clamp(Opt("--count", 4), 1, 125);
var sweep = Opt("--sweep", 0);
var timeoutMs = Opt("--timeout", 3000);
var offsets = (args.Contains("--offsets")
        ? args[Array.IndexOf(args, "--offsets") + 1].Split(',', StringSplitOptions.RemoveEmptyEntries)
        : ["-4", "-2", "-1", "0", "1", "2", "4"])
    .Select(s => int.Parse(s, CultureInfo.InvariantCulture))
    .ToArray();

// —— 对每个候选偏移读一次 —— //
Console.WriteLine($"目标 {host}:{port} unit={unit} 基准地址={start} 数量={count} 扫描±{sweep}");
Console.WriteLine("偏移".PadRight(8) + "结果（十进制 | 每寄存器 hex，大端字序）");
foreach (var offset in offsets)
{
    var addr = start + offset - sweep; // 扫描时从基准-负向sweep开始连读 count+2*sweep 个
    var n = Math.Clamp(count + 2 * sweep, 1, 125);
    try
    {
        var regs = ReadHoldingRegisters(host, port, unit, (ushort)addr, n, timeoutMs);
        var dec = string.Join(' ', regs);
        var hex = string.Join(' ', regs.Select(r => r.ToString("X4")));
        Console.WriteLine($"{offset:+0;-0;0}".PadRight(8) + $"OK  {dec}  |  {hex}");
    }
    catch (ModbusException ex)
    {
        Console.WriteLine($"{offset:+0;-0;0}".PadRight(8) + $"异常码 0x{ex.Code:X2}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{offset:+0;-0;0}".PadRight(8) + $"失败  {ex.Message}");
    }
}
Console.WriteLine("与 PLC 侧已知期望值（如心跳递增计数）比对，命中行即真实偏移。");
return 0;

// —— 最小 Modbus TCP 实现：读保持寄存器（功能码 0x03）—— //
static ushort[] ReadHoldingRegisters(string host, int port, byte unit, ushort addr, int count, int timeoutMs)
{
    using var client = new TcpClient();
    var connect = client.ConnectAsync(host, port);
    if (!connect.Wait(timeoutMs) || !client.Connected)
        throw new IOException($"连接超时（{timeoutMs} ms）");
    client.ReceiveTimeout = client.SendTimeout = timeoutMs;
    var stream = client.GetStream();

    // MBAP 头：事务号(2) 协议号(2) 长度(2) 单元号(1)；PDU：功能码(1) 起始地址(2) 数量(2)
    byte[] request = [0, 1, 0, 0, 0, 6, unit, 0x03, (byte)(addr >> 8), (byte)addr, (byte)(count >> 8), (byte)count];
    stream.Write(request);

    var header = ReadExactly(stream, 7); // TID(2)+PID(2)+LEN(2)+UID(1)
    var remaining = (header[4] << 8 | header[5]) - 1;
    var pdu = ReadExactly(stream, remaining);

    if (pdu[0] == 0x83) // 异常响应
        throw new ModbusException(pdu.Length > 1 ? pdu[1] : (byte)0);
    if (pdu[0] != 0x03 || pdu[1] != count * 2)
        throw new IOException("响应格式不符");

    var regs = new ushort[count];
    for (var i = 0; i < count; i++)
        regs[i] = (ushort)(pdu[2 + 2 * i] << 8 | pdu[3 + 2 * i]);
    return regs;
}

static byte[] ReadExactly(NetworkStream stream, int n)
{
    var buf = new byte[n];
    var read = 0;
    while (read < n)
    {
        var got = stream.Read(buf, read, n - read);
        if (got <= 0) throw new IOException("连接提前关闭");
        read += got;
    }
    return buf;
}

sealed class ModbusException(byte code) : Exception($"Modbus 异常响应码 0x{code:X2}")
{
    public byte Code { get; } = code;
}
