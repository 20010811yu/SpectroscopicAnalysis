using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.DeviceInterface;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.SctStrategy;

namespace SpectroscopicAnalysis.Host.Workflow;

/// <summary>
/// 流程编排器（编排设计文档）：从策略引擎出队任务 → 预检 → 经设备抽象推进状态机 → 回执并回喂引擎。
/// 设备访问全部经 DeviceInterface 抽象（Stub 实现）；超时/重试/轮询取 WorkflowOptions 集中配置。
/// </summary>
public sealed class WorkflowOrchestrator : IWorkflowOrchestrator
{
    private readonly ISctStrategyEngine _engine;
    private readonly ISendTask _send;
    private readonly IGetTaskStatus _deviceStatus;
    private readonly IGetSafetyStatus _safety;
    private readonly ISpectrometer _spectrometer;
    private readonly ISctOperationLogRepository _logs;
    private readonly IErrorCollector _errors;
    private readonly WorkflowOptions _options;

    private readonly object _gate = new();
    private readonly WorkflowLocks _locks = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _lastEngineTick = DateTime.MinValue;

    private SctTask? _current;
    private string _taskState = TaskState.Idle;
    private string? _sctSubState;
    private volatile bool _emergency;

    /// <summary>编排器事件上抛（UI 经 IUiDispatcher 切线程，本层不引 WPF 类型）。</summary>
    public event EventHandler<WorkflowStateChangedEventArgs>? StateChanged;

    public WorkflowOrchestrator(
        ISctStrategyEngine engine,
        ISendTask send,
        IGetTaskStatus deviceStatus,
        IGetSafetyStatus safety,
        ISpectrometer spectrometer,
        ISctOperationLogRepository logs,
        IErrorCollector errors,
        WorkflowOptions? options = null)
    {
        _engine = engine;
        _send = send;
        _deviceStatus = deviceStatus;
        _safety = safety;
        _spectrometer = spectrometer;
        _logs = logs;
        _errors = errors;
        _options = options ?? new WorkflowOptions();
    }

    /// <inheritdoc />
    public WorkflowSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new WorkflowSnapshot(
                    _current?.TaskId,
                    _current?.TaskType ?? string.Empty,
                    _current?.SampleId,
                    _taskState,
                    _sctSubState,
                    IsRunning: _loop is not null && !_loop.IsCompleted,
                    _locks.Occupancy);
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        // 启动自检：状态机转移表写错立即暴露（编排设计文档 §4）
        TaskState.ValidateTransitions();
        _emergency = false;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => MainLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // 优雅停止的预期路径
            }
        }
    }

    /// <inheritdoc />
    public async Task EmergencyStopAsync(string reason)
    {
        _emergency = true;
        _cts?.Cancel();
        _engine.EmergencyReset();   // 清空队列（全部 Cancelled）+ 冻结闭环（引擎设计 §10）
        _errors.Collect(ErrorCodes.EmergencyStop, $"工作流急停：{reason}", _current?.SampleId);
        WriteTrace(_current?.SampleId, "EmergencyStop", $"急停：{reason}", "Fail");
        await StopAsync(CancellationToken.None);
        lock (_gate)
        {
            _current = null;
            _taskState = TaskState.Idle;
            _sctSubState = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        await StopAsync(CancellationToken.None);
    }

    // ==================== 主循环 ====================

    private async Task MainLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_emergency)
            {
                return;     // 急停后主循环退出，恢复须人工复位后重新 StartAsync
            }

            if (_engine.TryDequeue(out var task))
            {
                try
                {
                    await ExecuteTaskAsync(task, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 单任务失败不拖垮主循环（引擎设计 §6.2：失败不自动重试，走错误链）
                    _engine.CompleteTask(task.TaskId, success: false, ex.Message);
                    _errors.Collect(ErrorCodes.UnhandledException, $"SCT 任务 {task.TaskType} 编排失败", task.SampleId, ex);
                    WriteTrace(task.SampleId, task.TaskType, $"编排异常：{ex.Message}", "Fail");
                }
                finally
                {
                    lock (_gate)
                    {
                        _current = null;
                        _taskState = TaskState.Idle;
                        _sctSubState = null;
                    }
                    RaiseStateChanged();
                }

                continue;
            }

            // 策略引擎时钟节拍（定时/按时间触发判定）
            var now = DateTime.UtcNow;
            if (now - _lastEngineTick >= TimeSpan.FromMilliseconds(_options.EngineTickIntervalMs))
            {
                _lastEngineTick = now;
                _engine.OnTick();
            }

            await Task.Delay(_options.PollIntervalMs, ct);
        }
    }

    // ==================== 任务执行 ====================

    /// <summary>执行单任务：预检 → 按类型编排 → 回执（编排设计文档 §5/§6）。</summary>
    private async Task ExecuteTaskAsync(SctTask task, CancellationToken ct)
    {
        lock (_gate)
        {
            _current = task;
            _taskState = TaskState.Registered;
        }
        RaiseStateChanged();

        // 预检（开发规范 11.3：SafetyOK/RobotReady/SparkStandReady/样品位置一致性，不可跳过）
        var safety = await _safety.GetSafetyStatusAsync(ct);
        if (!safety.AllPassed)
        {
            _errors.Collect(ErrorCodes.SctSampleMaintainFailed,
                $"预检未通过（Safety={safety.SafetyOk} Robot={safety.RobotReady} Spark={safety.SparkStandReady} 位置一致={safety.SamplePositionMatch}）",
                task.SampleId);
            WriteTrace(task.SampleId, task.TaskType, "预检未通过，任务失败", "Fail");
            _engine.CompleteTask(task.TaskId, success: false, "预检未通过");
            return;
        }

        switch (task.TaskType)
        {
            case SctTaskType.HeightMeasure:
                await RunHeightMeasureAsync(task, ct);
                break;
            case SctTaskType.Standardize:
            case SctTaskType.TypeStandardize:
                await RunSctAnalysisAsync(task, withCoeffUpdate: true, ct);
                break;
            case SctTaskType.ControlCheck:
                await RunControlCheckAsync(task, ct);
                break;
            case SctTaskType.Production:
                await RunProductionAsync(task, ct);
                break;
            default:
                _engine.CompleteTask(task.TaskId, success: true, null);
                return;
        }

        _engine.CompleteTask(task.TaskId, success: true, null);
        WriteTrace(task.SampleId, task.TaskType, "任务完成", "Success");
    }

    /// <summary>生产任务：Registered→…→Archiving（编排设计文档 §5；激发结果回喂引擎）。</summary>
    private async Task RunProductionAsync(SctTask task, CancellationToken ct)
    {
        var sampleId = task.SampleId ?? "Unknown";
        await TransitionToAsync(TaskState.Preparing, ct);
        await _send.SendCycleStartAsync(idCycle: 1, sampleId, gripHeight: 0, ct);   // 抓取（含抓取高度，当前默认 0）

        await TransitionToAsync(TaskState.Milling, ct);
        await TransitionToAsync(TaskState.Placing, ct);

        await TransitionToAsync(TaskState.Analyzing, ct);
        await _locks.RunWithLockAsync(WorkflowLockName.SparkStand, _options.LockTimeoutMs, async () =>
        {
            await _spectrometer.TriggerSparkAsync(sampleId, ct);
            await _spectrometer.ReadResultAsync(ct);
            return true;
        }, ct);

        // 系数修正计算归 Correction 模块（编排设计文档 §2：本层不做计算）
        await TransitionToAsync(TaskState.Archiving, ct);
        await _locks.RunWithLockAsync(WorkflowLockName.SampleArchive, _options.LockTimeoutMs, async () =>
        {
            await Task.Delay(1, ct);    // Stub：归档指令占位
            return true;
        }, ct);

        await TransitionToAsync(TaskState.Completed, ct);
        _engine.NotifyAnalysisCompleted(sampleId);
    }

    /// <summary>SCT 分析类任务（标准化/类型标准化）：SCT 子状态链 + 系数更新 + 引擎回喂（编排设计文档 §6）。</summary>
    private async Task RunSctAnalysisAsync(SctTask task, bool withCoeffUpdate, CancellationToken ct)
    {
        var sampleId = task.SampleId ?? "Unknown";
        await EnterSubStateAsync(SctSubState.Selecting, ct);

        await EnterSubStateAsync(SctSubState.Retrieving, ct);
        var idCycle = task.TaskType == SctTaskType.ControlCheck ? 3 : 2;
        await _locks.RunWithLockAsync(WorkflowLockName.Robot, _options.LockTimeoutMs, async () =>
        {
            await _send.SendCycleStartAsync(idCycle, sampleId, gripHeight: 0, ct);
            return true;
        }, ct);

        await EnterSubStateAsync(SctSubState.Milling, ct);
        await EnterSubStateAsync(SctSubState.Analyzing, ct);
        await _locks.RunWithLockAsync(WorkflowLockName.SparkStand, _options.LockTimeoutMs, async () =>
        {
            await _spectrometer.TriggerSparkAsync(sampleId, ct);
            await _spectrometer.ReadResultAsync(ct);
            return true;
        }, ct);

        if (withCoeffUpdate)
        {
            // 标准化：新系数版本写入归 Correction 模块（编排设计文档 §2）；完成即回喂引擎推进闭环
            await EnterSubStateAsync(SctSubState.UpdatingCoeff, ct);
        }

        await EnterSubStateAsync(SctSubState.Returning, ct);
        _engine.NotifyAnalysisCompleted(sampleId);
        if (withCoeffUpdate)
        {
            _engine.NotifyStandardizeCompleted(sampleId);
        }
    }

    /// <summary>控制检查：分析后按激发结果回喂判定（Stub 阶段 passed=激发成功；Correction 落地后按 LimitType/LimitValue 精确判定）。</summary>
    private async Task RunControlCheckAsync(SctTask task, CancellationToken ct)
    {
        var sampleId = task.SampleId ?? "Unknown";
        var passed = await RunSctAnalysisWithResultAsync(task, ct);
        _engine.NotifyControlResult(sampleId, passed);
    }

    /// <summary>带激发结果的 SCT 分析链（控制检查用；返回激发是否成功）。</summary>
    private async Task<bool> RunSctAnalysisWithResultAsync(SctTask task, CancellationToken ct)
    {
        var sampleId = task.SampleId ?? "Unknown";
        await EnterSubStateAsync(SctSubState.Selecting, ct);

        await EnterSubStateAsync(SctSubState.Retrieving, ct);
        await _locks.RunWithLockAsync(WorkflowLockName.Robot, _options.LockTimeoutMs, async () =>
        {
            await _send.SendCycleStartAsync(idCycle: 3, sampleId, gripHeight: 0, ct);
            return true;
        }, ct);

        await EnterSubStateAsync(SctSubState.Milling, ct);
        await EnterSubStateAsync(SctSubState.Analyzing, ct);
        var spark = await _locks.RunWithLockAsync(WorkflowLockName.SparkStand, _options.LockTimeoutMs, async () =>
        {
            await _spectrometer.TriggerSparkAsync(sampleId, ct);
            return await _spectrometer.ReadResultAsync(ct);
        }, ct);

        await EnterSubStateAsync(SctSubState.Returning, ct);
        return spark.Success;
    }

    /// <summary>测高任务：不激发，回读厚度回填策略引擎（编排设计文档 §6 特殊流）。</summary>
    private async Task RunHeightMeasureAsync(SctTask task, CancellationToken ct)
    {
        var sampleId = task.SampleId ?? "Unknown";
        await EnterSubStateAsync(SctSubState.Selecting, ct);

        await EnterSubStateAsync(SctSubState.Retrieving, ct);
        await _locks.RunWithLockAsync(WorkflowLockName.Robot, _options.LockTimeoutMs, async () =>
        {
            await _send.MeasureHeightAsync(sampleId, ct);
            return true;
        }, ct);

        await EnterSubStateAsync(SctSubState.MeasuringHeight, ct);
        var status = await _deviceStatus.GetTaskStatusAsync(ct);
        var thickness = status.MeasuredThickness
            ?? throw new InvalidOperationException("测高任务未回读厚度");

        await EnterSubStateAsync(SctSubState.Returning, ct);
        _engine.NotifyHeightMeasured(sampleId, thickness);
    }

    // ==================== 状态机守卫与辅助 ====================

    /// <summary>任务状态转移（带守卫：非法跳跃拒绝并记录）。</summary>
    private async Task TransitionToAsync(string nextState, CancellationToken ct)
    {
        string current;
        lock (_gate)
        {
            current = _taskState;
            if (!TaskState.CanTransition(current, nextState))
            {
                _errors.Collect(ErrorCodes.UnhandledException, $"非法任务状态转移：{current} → {nextState}", _current?.SampleId);
                throw new InvalidOperationException($"非法任务状态转移：{current} → {nextState}");
            }

            _taskState = nextState;
        }

        RaiseStateChanged();
        await Task.Delay(1, ct);    // Stub：真实设备为状态轮询等待
    }

    /// <summary>SCT 子状态推进（沿链顺序，守卫链内顺序即合法）。</summary>
    private async Task EnterSubStateAsync(string subState, CancellationToken ct)
    {
        lock (_gate)
        {
            _sctSubState = subState;
        }

        RaiseStateChanged();
        await Task.Delay(1, ct);
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, new WorkflowStateChangedEventArgs(Snapshot));

    /// <summary>轨迹审计（任务清单第 12 节：每步动作留痕）。</summary>
    private void WriteTrace(string? sampleId, string operationType, string reason, string resultStatus)
    {
        try
        {
            _logs.Insert(new SctOperationLog
            {
                SampleId = sampleId,
                OperationType = operationType switch
                {
                    SctTaskType.Standardize => "Standardization",
                    SctTaskType.ControlCheck => "ControlCheck",
                    SctTaskType.HeightMeasure => "HeightMeasure",
                    _ => operationType,
                },
                TriggerReason = reason,
                OperationTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ResultStatus = resultStatus,
            });
        }
        catch (Exception ex)
        {
            _errors.Collect(ErrorCodes.SctSampleMaintainFailed, "Workflow 写轨迹日志失败", sampleId, ex);
        }
    }
}
