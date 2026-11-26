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
    private readonly ISctPolicyStore? _policyStore;
    private readonly IProductionSampleRepository _production;
    private readonly IErrorCollector _errors;
    private readonly LoggingAlarm.ISystemLogCollector _systemLog;
    private readonly WorkflowOptions _options;

    private readonly object _gate = new();
    private readonly WorkflowLocks _locks = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _lastEngineTick = DateTime.MinValue;

    /// <summary>最近一次类型标准化的合金类型（生产流程前置合金切换检测基准；系数有效性由类型标准化建立，生产本身不改变基准；null=尚无记录）。</summary>
    private string? _lastTypeStdAlloy;

    /// <summary>已入队前置类型标准化等待执行的目标合金（生产任务开始执行后清空；防同合金重复入队）。</summary>
    private string? _pendingPreStdAlloy;

    /// <summary>最近一次"无匹配类型标准化样"报警键（样品:合金；防主循环轮询重复报警）。</summary>
    private string? _alloySwitchAlarmKey;

    private SctTask? _current;
    private string _taskState = TaskState.Idle;

    /// <summary>当前任务开始时间（生产样=注册提交时刻/入队时间；SCT 样=指令下发时刻；主循环单任务串行，字段安全）。</summary>
    private DateTime? _currentTaskStartTime;

    /// <summary>当前任务执行开始时刻（UTC；生产流程补足 TaskDurationMs 的计时基准）。</summary>
    private DateTime _taskStartedUtc;
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
        WorkflowOptions? options = null,
        ISctPolicyStore? policyStore = null)
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
        _policyStore = policyStore;
    }

    /// <summary>样品是否为控制样孪生（自身 TypeStd 且 PairSampleId 指向 Control 样）——与引擎 IsControlTwin 同规则。</summary>
    private bool IsControlTwin(string? sampleId)
    {
        if (string.IsNullOrEmpty(sampleId))
        {
            return false;
        }

        var sample = _samples.GetById(sampleId);
        if (sample is null || sample.SampleType != SampleType.TypeStd || string.IsNullOrEmpty(sample.PairSampleId))
        {
            return false;
        }

        var pair = _samples.GetById(sample.PairSampleId);
        return pair is not null && pair.SampleType == SampleType.Control;
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
        _systemLog.Log("应用", "流程编排启动");   // 全流程日志：系统启停动作留痕
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
        _systemLog.Log("应用", "流程编排停止");
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

            // 策略引擎时钟节拍（定时/按时间触发判定）：无论队列是否空闲都照常判定入队，
            // 到期的定时标准化/检查样/类型标准化先进入任务队列（与其他任务同样排队可见）再依优先级执行
            var now = DateTime.UtcNow;
            if (now - _lastEngineTick >= TimeSpan.FromMilliseconds(_options.EngineTickIntervalMs))
            {
                _lastEngineTick = now;
                _engine.OnTick();
            }

            // 出队前合金切换检测：队首为生产任务且合金切换时，先入队前置类型标准化（P3 优先自动先执行）
            CheckAlloySwitchBeforeDequeue();

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

            await Task.Delay(_options.PollIntervalMs, ct);
        }
    }

    // ==================== 任务执行 ====================

    /// <summary>执行单任务：预检 → 按类型编排 → 回执（编排设计文档 §5/§6）。</summary>
    private async Task ExecuteTaskAsync(SctTask task, CancellationToken ct)
    {
        _taskStartedUtc = DateTime.UtcNow;   // 执行时长起点（生产流程补足 TaskDurationMs 用；与日志 StartTime 口径不同）
        // 开始时间采集：统一为任务实际开始执行时刻（生产样不再取入队时间，消除日志区间与 SCT 任务重叠的错觉）
        _currentTaskStartTime = DateTime.Now;
        lock (_gate)
        {
            _current = task;
            _taskState = TaskState.Registered;
        }
        RaiseStateChanged();
        // 全流程日志：任务开始（关键节点级，此前仅完成行有记录，执行期间主控台不可见）
        _systemLog.Log(LogCategoryOf(task.TaskType),
            string.IsNullOrEmpty(task.SampleId)
                ? $"任务开始：{TaskTypeNameOf(task.TaskType)}"
                : $"任务开始（样品 {task.SampleId}）：{TaskTypeNameOf(task.TaskType)}");

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
        // 全流程日志：预检通过（此前仅失败留痕，成功无记录）
        _systemLog.Log(LogCategoryOf(task.TaskType),
            string.IsNullOrEmpty(task.SampleId)
                ? "安全预检通过：SafetyOK/RobotReady/SparkStandReady/位置一致"
                : $"安全预检通过（样品 {task.SampleId}）：SafetyOK/RobotReady/SparkStandReady/位置一致");

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
                if (task.TaskType == SctTaskType.TypeStandardize && !string.IsNullOrWhiteSpace(task.SampleId))
                {
                    // 类型标准化完成：系数基准切至该合金（此后生产同合金免检，异合金仍触发前置标准化）
                    _lastTypeStdAlloy = _samples.GetAll()
                        .FirstOrDefault(s => string.Equals(s.SampleId, task.SampleId, StringComparison.Ordinal))?.AlloyType;
                }

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

        // 模拟真实流程耗时：生产任务在 RunProductionAsync 内补足（见该处注释），其余类型步骤瞬时即回执

        _engine.CompleteTask(task.TaskId, success: true, null);
        // 一行一任务：触发原因携带任务下发原因（原入队行信息并入完成行，StartTime=入队/下发时刻）；
        // 控制检查/测高的结果数据（合格判定、厚度前后值）由编排层并入本行——引擎回喂不再单独写日志
        var resultStatus = heightBelowMin ? "Fail" : "Success";
        WriteTrace(task.SampleId, task.TaskType, task.Reason, resultStatus,
            _currentTaskStartTime?.ToString("yyyy-MM-dd HH:mm:ss"), isPass, thicknessBefore, thicknessAfter);
        // 任务执行链可能回喂引擎更新样品档案（使用次数/状态/厚度），通知台账类页面重载
        _sampleDataChanged.NotifySampleDataChanged();
    }

    /// <summary>模拟真实流程耗时：步骤瞬时（Stub）时等待补足整体执行时长，激发/归档/完成随之落在流程末尾。</summary>
    private async Task WaitTaskDurationAsync(CancellationToken ct)
    {
        var remain = TimeSpan.FromMilliseconds(_options.TaskDurationMs) - (DateTime.UtcNow - _taskStartedUtc);
        if (remain > TimeSpan.Zero)
        {
            await Task.Delay(remain, ct);
        }
    }

    /// <summary>生产任务：Registered→…→Archiving（编排设计文档 §5；激发结果回喂引擎）。
    /// 开始时记录本次合金作为后续合金切换检测的"上一个生产样"基准。</summary>
    private async Task RunProductionAsync(SctTask task, CancellationToken ct)
    {
        var sampleId = task.SampleId ?? "Unknown";
        var currentAlloy = _production.GetAll().FirstOrDefault(p => p.SampleId == sampleId)?.AlloyType;
        _pendingPreStdAlloy = null;             // 本生产任务已开始执行，其前置标准化等待结束
        if (!string.IsNullOrWhiteSpace(currentAlloy))
        {
            _engine.UpdateCurrentAlloy(currentAlloy);   // 同步引擎当前合金（期限届满判定用）
        }

        await TransitionToAsync(TaskState.Preparing, ct);
        await _send.SendCycleStartAsync(idCycle: 1, sampleId, gripHeight: 0, ct);   // 抓取（含抓取高度，当前默认 0）

        await TransitionToAsync(TaskState.Milling, ct);
        await TransitionToAsync(TaskState.Placing, ct);

        // 模拟真实流程耗时：抓取/铣样/放置瞬时（Stub）时在此等待补足整体时长，
        // 使激发、归档、完成都落在流程末尾（分析进行中状态持续约 TaskDurationMs）
        await WaitTaskDurationAsync(ct);

        await TransitionToAsync(TaskState.Analyzing, ct);
        var spark = await _locks.RunWithLockAsync(WorkflowLockName.SparkStand, _options.LockTimeoutMs, async () =>
        {
            await _spectrometer.TriggerSparkAsync(sampleId, ct);
            return await _spectrometer.ReadResultAsync(ct);
        }, ct);
        if (spark.Success)
        {
            ArchiveSparkResult(sampleId, spark);   // 激发结果归档（Analysis_Result；生产样无 SCT 档案，无使用次数记录）
            // 生产样状态流转：分析完成置 Completed（ProductionSample 模型注释语义；此前无写入点导致 KPI「今日已完成」恒 0）
            _production.UpdateStatus(sampleId, ProductionStatus.Completed);
        }
        else
        {
            // 激发失败：保持待分析并记失败原因（不进自动重试，人工处理后重新分析）
            _production.UpdateStatus(sampleId, ProductionStatus.Pending, "激发失败，待重新分析");
        }

        // 系数修正计算归 Correction 模块（编排设计文档 §2：本层不做计算）
        await TransitionToAsync(TaskState.Archiving, ct);
        await _locks.RunWithLockAsync(WorkflowLockName.SampleArchive, _options.LockTimeoutMs, async () =>
        {
            await Task.Delay(1, ct);    // Stub：归档指令占位
            return true;
        }, ct);

        await TransitionToAsync(TaskState.Completed, ct);
        _engine.NotifyAnalysisCompleted(sampleId, task.TaskType);
    }

    /// <summary>
    /// 生产前合金切换检测（需求：相邻生产样合金不同时先做本次合金的类型标准化，且标准化须加入任务队列）：
    /// 主循环空闲出队前检查队首——若为生产任务且合金与最近一次类型标准化不同（或基准未知：无类型标准化历史/档案缺失/合金为空，
    /// 均视作需要类型标准化），则将匹配合金的类型标准化任务提交引擎队列（类型标准化 P3 优先于生产 P5，
    /// 自动先于生产出队执行，队列表同步可见）。
    /// 无匹配类型标准化样 → 5103 报警一次不阻断（与引擎选样失败同语义），生产照常继续。
    /// </summary>
    private void CheckAlloySwitchBeforeDequeue()
    {
        var next = _engine.GetQueuedTasks().FirstOrDefault();
        if (next is null || next.TaskType != SctTaskType.Production || string.IsNullOrEmpty(next.SampleId))
        {
            return;     // 仅生产任务需要前置合金检测
        }

        var currentAlloy = _production.GetAll().FirstOrDefault(p => p.SampleId == next.SampleId)?.AlloyType;
        if (string.IsNullOrWhiteSpace(currentAlloy))
        {
            return;     // 合金未知：无可比基准，直接生产
        }

        // 重启后首次检测：从运行日志恢复最近一次类型标准化的合金作为比对基准（跨重启延续检测）
        if (string.IsNullOrWhiteSpace(_lastTypeStdAlloy))
        {
            _lastTypeStdAlloy = GetLastTypeStdAlloyFromLog();
            if (!string.IsNullOrWhiteSpace(_lastTypeStdAlloy))
            {
                _systemLog.Log("策略引擎",
                    $"生产合金切换检测：从运行日志恢复最近一次类型标准化合金 {_lastTypeStdAlloy}（重启延续检测基准）", "Info");
            }
        }

        // 已为该合金入队前置标准化（P3 排在队首生产任务之前），等待其执行即可，避免重复入队
        if (_pendingPreStdAlloy == currentAlloy.Trim())
        {
            return;
        }

        // 基准合金未知（日志无生产历史 / 上次生产样已删除或合金为空）同样需要前置类型标准化：
        // 系数对当前合金是否有效无法确认，按"类型不匹配"处理，先做本次合金的类型标准化再生产
        if (!string.IsNullOrWhiteSpace(_lastTypeStdAlloy) &&
            string.Equals(_lastTypeStdAlloy.Trim(), currentAlloy.Trim(), StringComparison.Ordinal))
        {
            return;     // 同合金：直接生产
        }

        // 合金切换（或基准未知）：选本次合金匹配的类型标准化样（TypeStd/Normal/启用，使用次数最少优先——与引擎选样规则一致）；
        // 未混用时排除控制样孪生（兼任类型标准化的检查样孪生档案按控制样对待，类型互斥，与引擎选样/校验同规则）
        var mixed = _policyStore?.Load().ControlTypeStdMixed == true;
        var typeStd = _samples.GetAll()
            .Where(s => s.SampleType == SampleType.TypeStd && s.Status == SampleStatus.Normal && s.IsActive)
            .Where(s => mixed || !IsControlTwin(s.SampleId))
            .Where(s => string.Equals(s.AlloyType?.Trim(), currentAlloy.Trim(), StringComparison.Ordinal))
            .OrderBy(s => s.UsageCount)
            .FirstOrDefault();

        if (typeStd is null)
        {
            // 无匹配类型标准化样：报警（5103 同语义）不阻断生产；每个样品+合金组合只报一次防轮询刷屏
            var alarmKey = $"{next.SampleId}:{currentAlloy}";
            if (_alloySwitchAlarmKey != alarmKey)
            {
                _alloySwitchAlarmKey = alarmKey;
                _errors.Collect(ErrorCodes.SctTypeStdUnavailable,
                    $"合金 {currentAlloy} 无匹配的类型标准化样，跳过前置类型标准化", next.SampleId);
                _systemLog.Log("策略引擎",
                    $"生产合金切换检测：{_lastTypeStdAlloy} → {currentAlloy}（样品 {next.SampleId}），无匹配类型标准化样", "Warning");
            }
            return;
        }

        // 前置类型标准化入队（P3 优先于 P5 生产，自动先出队执行；引擎同类型同目标去重兜底）
        var baselineText = string.IsNullOrWhiteSpace(_lastTypeStdAlloy) ? "未知" : _lastTypeStdAlloy;
        var reason = $"生产前置类型标准化（{baselineText}→{currentAlloy}）";
        _engine.SubmitTask(SctTaskType.TypeStandardize, typeStd.SampleId, reason);
        _systemLog.Log("策略引擎",
            $"生产合金切换检测：{baselineText} → {currentAlloy}（样品 {next.SampleId}），前置类型标准化（样品 {typeStd.SampleId}）已加入任务队列", "Warning");
        _pendingPreStdAlloy = currentAlloy.Trim();
    }

    /// <summary>从运行日志恢复最近一次类型标准化的合金（最近一条成功 TypeStandardize 记录的样品 → 类型标准化样档案合金；无记录返回 null 视为基准未知）。</summary>
    private string? GetLastTypeStdAlloyFromLog()
    {
        try
        {
            var lastSampleId = _logs.GetAll()
                .Where(l => l.OperationType == SctTaskType.TypeStandardize &&
                            l.ResultStatus == "Success" &&
                            !string.IsNullOrEmpty(l.SampleId))
                .OrderByDescending(l => l.OperationTime)
                .FirstOrDefault()?.SampleId;
            if (lastSampleId is null)
            {
                return null;
            }

            return _samples.GetAll()
                .FirstOrDefault(s => string.Equals(s.SampleId, lastSampleId, StringComparison.Ordinal))?.AlloyType;
        }
        catch (Exception ex)
        {
            // 恢复失败不阻断合金切换检测（退化为"基准未知"，仍会对首个生产样做前置类型标准化），仅记错误链
            _errors.Collect(ErrorCodes.UnhandledException, "从运行日志恢复最近一次类型标准化合金失败", null, ex);
            return null;
        }
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

        // 模拟真实流程耗时（与生产任务一致）：激发/系数更新/完成落在流程末尾
        await WaitTaskDurationAsync(ct);

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
        _engine.NotifyAnalysisCompleted(sampleId, task.TaskType);
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

        // 模拟真实流程耗时（与生产任务一致）：激发/归档/完成落在流程末尾
        await WaitTaskDurationAsync(ct);

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
            _systemLog.Log("设备操作", $"激发后样品档案已更新：使用次数+1（样品 {sampleId}）");   // 全流程日志：数据维护成功留痕
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
            _systemLog.Log("设备操作", $"激发结果双存完成（样品 {sampleId}）");   // 全流程日志：原始数据落库成功留痕
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

    /// <summary>按任务类型取全流程日志类目（检查样/标准化；其余归设备操作）——轨迹审计与系统日志共用。</summary>
    private static string LogCategoryOf(string operationType) => operationType switch
    {
        SctTaskType.ControlCheck => "检查样",
        SctTaskType.Standardize or SctTaskType.TypeStandardize => "标准化",
        _ => "设备操作",
    };

    /// <summary>按任务类型取中文类型名（完成行与任务开始行共用）。</summary>
    private static string TaskTypeNameOf(string operationType) => operationType switch
    {
        SctTaskType.ControlCheck => "校正检查",
        SctTaskType.Standardize => "标准化",
        SctTaskType.TypeStandardize => "类型标准化",
        SctTaskType.HeightMeasure => "测高",
        SctTaskType.Production => "生产分析",
        SctTaskType.Archive => "归档",
        _ => operationType,
    };

    /// <summary>轨迹审计（任务清单第 12 节：每步动作留痕）。</summary>
    /// <param name="startTime">开始时间（生产样=注册提交时刻；SCT 样=指令下发时刻；null=未采集/历史兼容）。</param>
    /// <param name="isPass">合格判定（控制检查；其余任务 null）。</param>
    /// <param name="thicknessBefore">测高前厚度（仅测高；其余 null）。</param>
    /// <param name="thicknessAfter">测高后厚度（仅测高；其余 null）。</param>
    private void WriteTrace(string? sampleId, string operationType, string reason, string resultStatus,
        string? startTime = null, bool? isPass = null, double? thicknessBefore = null, double? thicknessAfter = null)
    {
        // 全流程日志联动：按任务类型区分类目（检查样/标准化；其余归设备操作），主控台「运行日志」面板数据源之一
        var category = LogCategoryOf(operationType);
        // 消息正文用中文类型名（映射见 TaskTypeNameOf，与任务开始行共用）
        var typeText = TaskTypeNameOf(operationType);
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
