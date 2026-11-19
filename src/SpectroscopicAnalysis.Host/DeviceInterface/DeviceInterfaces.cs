namespace SpectroscopicAnalysis.Host.DeviceInterface;

/// <summary>
/// PLC 任务下发（命令握手：命令寄存器写值 + 应答寄存器读值模拟方法调用——任务清单第二节）。
/// 当前实现为 StubDeviceGateway（内存模拟）；后续替换为 Modbus TCP / OPC UA 实现，仅换 DI 注册。
/// </summary>
public interface ISendTask
{
    /// <summary>下发循环任务（CYCLE_START_REQUEST）；idCycle：1 生产 / 2 标准化 / 3 检查样。</summary>
    Task SendCycleStartAsync(int idCycle, string sampleId, double gripHeight, CancellationToken ct);

    /// <summary>下发测高指令（MeasureHeight；结果经 Workflow 回填策略引擎）。</summary>
    Task MeasureHeightAsync(string sampleId, CancellationToken ct);
}

/// <summary>PLC 任务状态轮询（状态获取采用轮询 + 变化检测）。</summary>
public interface IGetTaskStatus
{
    /// <summary>读取当前任务状态快照。</summary>
    Task<DeviceTaskStatus> GetTaskStatusAsync(CancellationToken ct);
}

/// <summary>安全信号读取（下发前预检 SafetyOK / RobotReady / SparkStandReady / 样品位置一致性）。</summary>
public interface IGetSafetyStatus
{
    /// <summary>读取安全信号快照。</summary>
    Task<SafetyStatus> GetSafetyStatusAsync(CancellationToken ct);
}

/// <summary>光谱仪（Host PC ↔ GNR，TCP/IP Socket + JSON，协议号 100–161，依据 v1.3 正式版）。</summary>
public interface ISpectrometer
{
    /// <summary>触发激发。</summary>
    Task TriggerSparkAsync(string sampleId, CancellationToken ct);

    /// <summary>读取激发结果（原始强度；修正计算归 Correction 模块）。</summary>
    Task<SparkResult> ReadResultAsync(CancellationToken ct);
}

/// <summary>心跳维护（递增计数寄存器，1–2 秒；中断超阈值进入安全状态——开发规范第 6 节）。</summary>
public interface IHeartbeat : IDisposable
{
    /// <summary>心跳中断事件（超阈值后触发一次）。</summary>
    event EventHandler<HeartbeatLostEventArgs>? HeartbeatLost;
}

/// <summary>任务结束通知（PLC 主动应答：SAMPLE_POSITIONED / 循环完成等）。</summary>
public interface INotifyTaskEnd
{
    /// <summary>任务结束事件。</summary>
    event EventHandler<TaskEndEventArgs>? TaskEnded;
}
