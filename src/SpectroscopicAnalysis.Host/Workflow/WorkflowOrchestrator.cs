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
    private readonly ISctSampleRepository _samples;
    private readonly IProductionSampleRepository _production;
    private readonly IErrorCollector _errors;
    private readonly LoggingAlarm.ISystemLogCollector _systemLog;
    private readonly WorkflowOptions _options;

    private readonly object _gate = new();
    private readonly WorkflowLocks _locks = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _lastEngineTick = DateTime.MinValue;

    private SctTask? _current;
    private string _taskState = TaskState.Idle;

    /// <summary>当前任务开始时间（生产样=注册提交时刻/入队时间；SCT 样=指令下发时刻；主循环单任务串行，字段安全）。</summary>
    private DateTime? _currentTaskStartTime;
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
        ISctSampleRepository samples,
        IProductionSampleRepository production,
        IErrorCollector errors,
        LoggingAlarm.ISystemLogCollector systemLog,
        SampleManager.ISampleDataChangeNotifier sampleDataChanged,
        WorkflowOptions? options = null)
    {
        _engine = engine;
        _send = send;
        _deviceStatus = deviceStatus;
        _safety = safety;
        _spectrometer = spectrometer;
        _logs = logs;
        _samples = samples;
        _production = production;
        _errors = errors;
        _systemLog = systemLog;
        _sampleDataChanged = sampleDataChanged;
        _options = options ?? new WorkflowOptions();
    }

    /// <summary>样品数据变更通知（任务回喂引擎更新样品档案后发布，样品台账类页面即时重载）。</summary>
    private readonly SampleManager.ISampleDataChangeNotifier _sampleDataChanged;

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
        // 开始时间采集：生产样 = 注册提交时刻（注册即入队，取任务入队时间）；其余类型在指令下发点采集
        _currentTaskStartTime = task.TaskType == SctTaskType.Production ? task.EnqueueTime : null;
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

        // 任务结果数据（并入完成日志行；仅控制检查/测高产生）
        bool? isPass = null;
        double? thicknessBefore = null;
        double? thicknessAfter = null;
        var heightBelowMin = false;

        switch (task.TaskType)
        {
            case SctTaskType.HeightMeasure:
                (thicknessBefore, thicknessAfter, var belowMin) = await RunHeightMeasureAsync(task, ct);
                if (belowMin)
                {
                    heightBelowMin = true;   // 低于最小厚度 → 置 Disabled，日志按失败留痕（引擎 §8 同语义）
                }

                break;
            case SctTaskType.Standardize:
            case SctTaskType.TypeStandardize:
                await RunSctAnalysisAsync(task, withCoeffUpdate: true, ct);
                break;
            case SctTaskType.ControlCheck:
                isPass = await RunControlCheckAsync(task, ct);
                break;
            case SctTaskType.Production:
                await RunProductionAsync(task, ct);
                break;
            default:
                _engine.CompleteTask(task.TaskId, success: true, null);
                return;
        }

        _engine.CompleteTask(task.TaskId, success: true, null);
        // 一行一任务：触发原因携带任务下发原因（原入队行信息并入完成行，StartTime=入队/下发时刻）；
        // 控制检查/测高的结果数据（合格判定、厚度前后值）由编排层并入本行——引擎回喂不再单独写日志
        var resultStatus = heightBelowMin ? "Fail" : "Success";
        WriteTrace(task.SampleId, task.TaskType, task.Reason, resultStatus,
            _currentTaskStartTime?.ToString("yyyy-MM-dd HH:mm:ss"), isPass, thicknessBefore, thicknessAfter);
        // 任务执行链可能回喂引擎更新样品档案（使用次数/状态/厚度），通知台账类页面重载
        _sampleDataChanged.NotifySampleDataChanged();
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
        var spark = await _locks.RunWithLockAsync(WorkflowLockName.SparkStand, _options.LockTimeoutMs, async () =>
        {
            await _spectrometer.TriggerSparkAsync(sampleId, ct);
            return await _spectrometer.ReadResultAsync(ct);
        }, ct);
        if (spark.Success)
        {
            ArchiveSparkResult(sampleId, spark);   // 激发结果归档（Analysis_Result；生产样无 SCT 档案，无使用次数记录）
        }

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
            _currentTaskStartTime = DateTime.Now;   // 开始时间 = 激发指令下发时刻
            return true;
        }, ct);

        await EnterSubStateAsync(SctSubState.Milling, ct);
        await EnterSubStateAsync(SctSubState.Analyzing, ct);
        var spark = await _locks.RunWithLockAsync(WorkflowLockName.SparkStand, _options.LockTimeoutMs, async () =>
        {
            await _spectrometer.TriggerSparkAsync(sampleId, ct);
            return await _spectrometer.ReadResultAsync(ct);
        }, ct);
        if (spark.Success)
        {
            PersistSparkUsage(sampleId);   // 激发成功：使用次数+1、上次使用时间落库（先于引擎回喂，阈值判定需最新次数）
            ArchiveSparkResult(sampleId, spark);   // 激发结果归档（Analysis_Result）
        }

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

    /// <summary>控制检查：分析后按激发结果回喂判定（Stub 阶段 passed=激发成功；Correction 落地后按 LimitType/LimitValue 精确判定）。
    /// 返回合格判定（并入完成日志行的 IsPass；引擎回喂不再单独写日志）。</summary>
    private async Task<bool> RunControlCheckAsync(SctTask task, CancellationToken ct)
    {
        var sampleId = task.SampleId ?? "Unknown";
        var passed = await RunSctAnalysisWithResultAsync(task, ct);
        _engine.NotifyControlResult(sampleId, passed);
        return passed;
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
            _currentTaskStartTime = DateTime.Now;   // 开始时间 = 激发指令下发时刻
            return true;
        }, ct);

        await EnterSubStateAsync(SctSubState.Milling, ct);
        await EnterSubStateAsync(SctSubState.Analyzing, ct);
        var spark = await _locks.RunWithLockAsync(WorkflowLockName.SparkStand, _options.LockTimeoutMs, async () =>
        {
            await _spectrometer.TriggerSparkAsync(sampleId, ct);
            return await _spectrometer.ReadResultAsync(ct);
        }, ct);
        if (spark.Success)
        {
            PersistSparkUsage(sampleId);   // 激发成功：使用次数+1、上次使用时间落库（先于引擎回喂）
            ArchiveSparkResult(sampleId, spark);   // 激发结果归档（Analysis_Result）
        }

        await EnterSubStateAsync(SctSubState.Returning, ct);
        return spark.Success;
    }

    /// <summary>
    /// 激发成功后持久化使用记录（仓储事务：使用次数+1、上次使用时间；厚度/状态 Stub 阶段保持原值，
    /// 真实激发后厚度待激发结果计算落地后回填）。生产样无 SCT 档案，跳过。
    /// </summary>
    private void PersistSparkUsage(string sampleId)
    {
        try
        {
            var sample = _samples.GetById(sampleId);
            if (sample is null)
            {
                return;
            }

            _samples.UpdateAfterSpark(sampleId, sample.CurrentThickness, sample.Status, DateTime.Now);
        }
        catch (Exception ex)
        {
            // 档案更新失败不阻断流程收尾，但必须进错误链（禁止吞异常）
            _errors.Collect(ErrorCodes.SctSampleMaintainFailed, "激发后样品档案更新失败", sampleId, ex);
        }
    }

    /// <summary>
    /// 激发结果归档（Analysis_Result，原始/校正双存缺口补齐）：原始强度按协议原文落库；
    /// 测定/校正浓度与系数版本待 GNR 真实协议的元素强度解析接入后回填（Stub 阶段为 null）。
    /// 仅生产样归档——表外键引用 Production_Sample（任务清单：生产样分析归档）；SCT 样审计在 SCT_OperationLog。
    /// 激发失败不归档（无结果数据）。归档失败进错误链，不阻断流程收尾。
    /// </summary>
    private void ArchiveSparkResult(string sampleId, SparkResult spark)
    {
        if (!spark.Success || !_production.GetAll().Any(s => s.SampleId == sampleId))
        {
            return;
        }

        try
        {
            _production.InsertResult(new AnalysisResult
            {
                SampleId = sampleId,
                RawIntensity = spark.RawIntensity,
                IsPass = true,
                ResultStatus = AnalysisResultStatus.Success,
                OperationTime = DateTime.Now,
            });
        }
        catch (Exception ex)
        {
            _errors.Collect(ErrorCodes.SctSampleMaintainFailed, "激发结果归档失败", sampleId, ex);
        }
    }

    /// <summary>
    /// 测高任务：不激发，回读厚度回填策略引擎（编排设计文档 §6 特殊流）。
    /// 返回（回填前厚度，回填后厚度，是否低于最小厚度）——并入完成日志行（引擎回喂不再单独写日志）。
    /// </summary>
    private async Task<(double? Before, double After, bool BelowMin)> RunHeightMeasureAsync(SctTask task, CancellationToken ct)
    {
        var sampleId = task.SampleId ?? "Unknown";
        await EnterSubStateAsync(SctSubState.Selecting, ct);

        await EnterSubStateAsync(SctSubState.Retrieving, ct);
        await _locks.RunWithLockAsync(WorkflowLockName.Robot, _options.LockTimeoutMs, async () =>
        {
            await _send.MeasureHeightAsync(sampleId, ct);
            _currentTaskStartTime = DateTime.Now;   // 开始时间 = 测高指令下发时刻
            return true;
        }, ct);

        await EnterSubStateAsync(SctSubState.MeasuringHeight, ct);
        var status = await _deviceStatus.GetTaskStatusAsync(ct);
        var thickness = status.MeasuredThickness
            ?? throw new InvalidOperationException("测高任务未回读厚度");

        // 回填前厚度/最小厚度阈值比较（引擎 §8 厚度流转语义，供完成日志行留痕）
        var sample = _samples.GetById(sampleId);
        var before = sample?.CurrentThickness;
        var belowMin = sample is { } s && thickness < s.MinThickness;

        await EnterSubStateAsync(SctSubState.Returning, ct);
        _engine.NotifyHeightMeasured(sampleId, thickness);
        return (before, thickness, belowMin);
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
    /// <param name="startTime">开始时间（生产样=注册提交时刻；SCT 样=指令下发时刻；null=未采集/历史兼容）。</param>
    /// <param name="isPass">合格判定（控制检查；其余任务 null）。</param>
    /// <param name="thicknessBefore">测高前厚度（仅测高；其余 null）。</param>
    /// <param name="thicknessAfter">测高后厚度（仅测高；其余 null）。</param>
    private void WriteTrace(string? sampleId, string operationType, string reason, string resultStatus,
        string? startTime = null, bool? isPass = null, double? thicknessBefore = null, double? thicknessAfter = null)
    {
        // 全流程日志联动：按任务类型区分类目（检查样/标准化；其余归设备操作），主控台「运行日志」面板数据源之一
        var category = operationType switch
        {
            SctTaskType.ControlCheck => "检查样",
            SctTaskType.Standardize or SctTaskType.TypeStandardize => "标准化",
            _ => "设备操作",
        };
        // 消息正文用中文类型名：控样→标准化 / 检查样→校正检查 / 类型标准化样→类型标准化
        var typeText = operationType switch
        {
            SctTaskType.ControlCheck => "校正检查",
            SctTaskType.Standardize => "标准化",
            SctTaskType.TypeStandardize => "类型标准化",
            SctTaskType.HeightMeasure => "测高",
            SctTaskType.Production => "生产分析",
            SctTaskType.Archive => "归档",
            _ => operationType,
        };
        _systemLog.Log(category,
            string.IsNullOrEmpty(sampleId) ? $"{typeText}：{reason}" : $"{typeText}（样品 {sampleId}）：{reason}",
            resultStatus == "Fail" ? "Failed" : "Info");
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
                StartTime = startTime,
                IsPass = isPass,
                ThicknessBefore = thicknessBefore,
                ThicknessAfter = thicknessAfter,
                OperationTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),   // 完成时间：全部流程执行完成后记录
                ResultStatus = resultStatus,
            });
        }
        catch (Exception ex)
        {
            _errors.Collect(ErrorCodes.SctSampleMaintainFailed, "Workflow 写轨迹日志失败", sampleId, ex);
        }
    }
}
