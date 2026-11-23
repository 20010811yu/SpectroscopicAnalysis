using System.Collections.Concurrent;

namespace SpectroscopicAnalysis.Host.DeviceInterface;

/// <summary>
/// 设备内存模拟网关（编排骨架阶段占位：PLC 寄存器映射与 GNR 协议落地前使用）。
/// 预检信号默认全通过、任务即时完成、激发返回成功结果；测试可注入失败/超差场景。
/// 后续 Modbus 真实现落地后仅替换 DI 注册（App.xaml.cs），Workflow 零改动。
/// PLC 数据交互经可选注入的 IPlcCommLogger 留痕（地址/时间/内容/方向；逻辑寄存器名，
/// Modbus 真实现替换后填真实寄存器地址）；GNR 激发交互不埋点（仅 PLC 专用）。
/// </summary>
public sealed class StubDeviceGateway :
    ISendTask, IGetTaskStatus, IGetSafetyStatus, ISpectrometer, IHeartbeat, INotifyTaskEnd
{
    /// <summary>逻辑寄存器地址（Stub 阶段占位名；Modbus 真实现后替换为真实寄存器地址）。</summary>
    private const string AddrCommand = "命令寄存器 4xxxx";
    private const string AddrStatus = "状态寄存器 4xxxx";
    private const string AddrSafety = "安全寄存器 4xxxx";
    private const string AddrHeartbeat = "心跳寄存器 4xxxx";

    private readonly ConcurrentDictionary<string, string> _taskStates = new();

    /// <summary>PLC 通信日志（可选；DI 注入单例，测试不传则不记录）。</summary>
    private readonly IPlcCommLogger? _commLog;

    /// <summary>全流程系统日志（可选；系统自动动作关键节点留痕，主控台「运行日志」面板消费）。</summary>
    private readonly LoggingAlarm.ISystemLogCollector? _systemLog;

    /// <summary>心跳计数（心跳寄存器递增值；每次安全状态轮询视为一次心跳节拍）。</summary>
    private int _heartbeatCounter;

    /// <summary>预检信号（测试可改为 false 模拟预检失败；默认全通过）。</summary>
    public bool SafetyOk { get; set; } = true;
    public bool RobotReady { get; set; } = true;
    public bool SparkStandReady { get; set; } = true;
    public bool SamplePositionMatch { get; set; } = true;

    /// <summary>激发是否成功（测试可模拟失败，用于超差闭环场景）。</summary>
    public bool SparkSucceeds { get; set; } = true;

    /// <summary>测高回读厚度（测试可设低于最小厚度，验证 Disabled 流转）。</summary>
    public double MeasuredThickness { get; set; } = 10;

    /// <summary>最近一次下发记录（idCycle → sampleId，供测试断言 ID_CYCLE 映射）。</summary>
    public List<(int IdCycle, string SampleId)> SentCycles { get; } = [];
    public List<string> MeasuredHeights { get; } = [];

    public StubDeviceGateway(IPlcCommLogger? commLog = null,
        LoggingAlarm.ISystemLogCollector? systemLog = null)
    {
        _commLog = commLog;
        _systemLog = systemLog;
    }

    /// <inheritdoc />
    public Task SendCycleStartAsync(int idCycle, string sampleId, double gripHeight, CancellationToken ct)
    {
        _commLog?.Log(PlcCommDirection.Send, AddrCommand,
            $"命令字={idCycle} 样品={sampleId} 抓取高度={gripHeight:0.##}");
        SentCycles.Add((idCycle, sampleId));
        _taskStates[sampleId] = DeviceTaskState.Running;
        TaskEnded?.Invoke(this, new TaskEndEventArgs(sampleId, DeviceTaskState.Running));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MeasureHeightAsync(string sampleId, CancellationToken ct)
    {
        _commLog?.Log(PlcCommDirection.Send, AddrCommand, $"命令字=测高 样品={sampleId}");
        MeasuredHeights.Add(sampleId);
        _taskStates[sampleId] = DeviceTaskState.Completed;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<DeviceTaskStatus> GetTaskStatusAsync(CancellationToken ct)
    {
        var status = new DeviceTaskStatus(DeviceTaskState.Completed, null, MeasuredThickness);
        _commLog?.Log(PlcCommDirection.Receive, AddrStatus,
            $"状态={status.State} 样品={status.SampleId ?? "—"} 测得厚度={status.MeasuredThickness:0.##}");
        return Task.FromResult(status);
    }

    /// <inheritdoc />
    public Task<SafetyStatus> GetSafetyStatusAsync(CancellationToken ct)
    {
        var status = new SafetyStatus(SafetyOk, RobotReady, SparkStandReady, SamplePositionMatch);
        _commLog?.Log(PlcCommDirection.Receive, AddrSafety,
            $"SafetyOK={status.SafetyOk} RobotReady={status.RobotReady} " +
            $"SparkStandReady={status.SparkStandReady} 位置一致={status.SamplePositionMatch}");
        return Task.FromResult(status);
    }

    /// <inheritdoc />
    public Task TriggerSparkAsync(string sampleId, CancellationToken ct)
    {
        _systemLog?.Log("光谱仪", $"激发测量已触发（样品 {sampleId}）");   // 全流程日志：GNR 激发动作留痕
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<SparkResult> ReadResultAsync(CancellationToken ct)
    {
        var result = new SparkResult("Stub", "1000", SparkSucceeds);
        // 全流程日志：激发结果接收（成功/失败区分级别）
        _systemLog?.Log("光谱仪",
            string.IsNullOrEmpty(result.SampleId)
                ? (result.Success ? "激发结果已接收：成功" : "激发结果已接收：失败")
                : $"激发结果已接收（样品 {result.SampleId}）：{(result.Success ? "成功" : "失败")}",
            result.Success ? "Info" : "Failed");
        return await Task.FromResult(result);
    }

    /// <inheritdoc />
    public event EventHandler<HeartbeatLostEventArgs>? HeartbeatLost;

    /// <inheritdoc />
    public event EventHandler<TaskEndEventArgs>? TaskEnded;

    /// <summary>测试辅助：模拟心跳中断。</summary>
    public void SimulateHeartbeatLost(string reason)
    {
        // 心跳丢失本身也是一次 PLC 交互留痕（计数寄存器停止递增的异常记录）
        _commLog?.Log(PlcCommDirection.Send, AddrHeartbeat, $"心跳丢失 原因={reason}");
        _systemLog?.Log("PLC", $"PLC 心跳丢失：{reason}", "Warning");   // 全流程日志：连接异常留痕
        HeartbeatLost?.Invoke(this, new HeartbeatLostEventArgs(reason));
    }

    /// <summary>
    /// 安全状态轮询同时驱动心跳计数递增（心跳寄存器 1–2 秒节拍）。
    /// 供编排主循环轮询调用；返回递增后的计数值便于测试断言。
    /// </summary>
    public int TickHeartbeat()
    {
        var counter = Interlocked.Increment(ref _heartbeatCounter);
        _commLog?.Log(PlcCommDirection.Send, AddrHeartbeat, $"计数值={counter}");
        return counter;
    }

    /// <inheritdoc />
    public void Dispose() { }
}
