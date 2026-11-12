using System.Collections.Concurrent;

namespace SpectroscopicAnalysis.Host.DeviceInterface;

/// <summary>
/// 设备内存模拟网关（编排骨架阶段占位：PLC 寄存器映射与 GNR 协议落地前使用）。
/// 预检信号默认全通过、任务即时完成、激发返回成功结果；测试可注入失败/超差场景。
/// 后续 Modbus 真实现落地后仅替换 DI 注册（App.xaml.cs），Workflow 零改动。
/// </summary>
public sealed class StubDeviceGateway :
    ISendTask, IGetTaskStatus, IGetSafetyStatus, ISpectrometer, IHeartbeat, INotifyTaskEnd
{
    private readonly ConcurrentDictionary<string, string> _taskStates = new();

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

    /// <inheritdoc />
    public Task SendCycleStartAsync(int idCycle, string sampleId, double gripHeight, CancellationToken ct)
    {
        SentCycles.Add((idCycle, sampleId));
        _taskStates[sampleId] = DeviceTaskState.Running;
        TaskEnded?.Invoke(this, new TaskEndEventArgs(sampleId, DeviceTaskState.Running));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MeasureHeightAsync(string sampleId, CancellationToken ct)
    {
        MeasuredHeights.Add(sampleId);
        _taskStates[sampleId] = DeviceTaskState.Completed;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<DeviceTaskStatus> GetTaskStatusAsync(CancellationToken ct) =>
        Task.FromResult(new DeviceTaskStatus(DeviceTaskState.Completed, null, MeasuredThickness));

    /// <inheritdoc />
    public Task<SafetyStatus> GetSafetyStatusAsync(CancellationToken ct) =>
        Task.FromResult(new SafetyStatus(SafetyOk, RobotReady, SparkStandReady, SamplePositionMatch));

    /// <inheritdoc />
    public Task TriggerSparkAsync(string sampleId, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<SparkResult> ReadResultAsync(CancellationToken ct) =>
        Task.FromResult(new SparkResult("Stub", "1000", SparkSucceeds));

    /// <inheritdoc />
    public event EventHandler<HeartbeatLostEventArgs>? HeartbeatLost;

    /// <inheritdoc />
    public event EventHandler<TaskEndEventArgs>? TaskEnded;

    /// <summary>测试辅助：模拟心跳中断。</summary>
    public void SimulateHeartbeatLost(string reason) =>
        HeartbeatLost?.Invoke(this, new HeartbeatLostEventArgs(reason));

    /// <inheritdoc />
    public void Dispose() { }
}
