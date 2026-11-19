using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.LoggingAlarm;

namespace SpectroscopicAnalysis.Host.SctStrategy;

/// <summary>
/// SCT 策略引擎实现（引擎设计文档）：消费 SctPolicyOptions 做触发判断与样品选择，
/// 维护 P29 优先级任务队列与超差闭环状态；厚度流转与审计写库经仓储接口。
/// 纯决策不含设备 I/O——设备调用由 Workflow 编排（Workflow 编排设计文档 §6）。
/// </summary>
public sealed class SctStrategyEngine : ISctStrategyEngine
{
    // ---- P29 固定优先级档位（引擎设计 §6.1；数值小者先执行，急停恢复由 Workflow 直接处理不入队） ----
    /// <summary>优先级 1：超差/人工触发的标准化（高于生产分析，任务清单第 5 节⑥）。</summary>
    public const int PriorityStandardize = 1;

    /// <summary>优先级 2：检查样检查。</summary>
    public const int PriorityControlCheck = 2;

    /// <summary>优先级 3：类型标准化。</summary>
    public const int PriorityTypeStandardize = 3;

    /// <summary>优先级 4：测高（标样可用的前置条件，优先于生产）。</summary>
    public const int PriorityHeightMeasure = 4;

    /// <summary>优先级 5：生产样品分析。</summary>
    public const int PriorityProduction = 5;

    /// <summary>优先级 6：归档。</summary>
    public const int PriorityArchive = 6;

    private readonly ISctPolicyStore _policyStore;
    private readonly ISctSampleRepository _samples;
    private readonly ISctOperationLogRepository _logs;
    private readonly IErrorCollector _errors;
    private readonly TimeProvider _time;

    private readonly object _gate = new();
    private readonly List<SctTask> _queue = [];

    /// <summary>已终结任务历史（Completed/Failed/Cancelled，最新在前，上限 50 条）——队列面板展示用。</summary>
    private readonly List<SctTask> _history = [];
    private SctTask? _running;
    private int _nextTaskId;

    // ---- 触发状态（内存态；进程重启后由执行记录/档案数据自然续接，不持久化） ----
    private DateTime? _lastControlCheckTime;
    private DateTime? _lastStandardizeTime;
    private DateTime? _lastTypeStdTime;
    private int _productionSinceControl;

    /// <summary>当前匹配的合金类型（NotifyAlloySwitched 更新；检查样合金匹配/类型标准化目标）。</summary>
    private string? _currentAlloy;

    /// <summary>超差闭环状态（null = 闭环未激活；引擎设计 §7）。</summary>
    private RecheckLoopState? _recheckLoop;

    /// <summary>是否切入过自动模式（NotifyModeChanged 边沿判定用）。</summary>
    private bool _isAutoMode;

    private sealed class RecheckLoopState
    {
        public required string ControlSampleId { get; init; }
        public int RecheckCount;
    }

    public SctStrategyEngine(
        ISctPolicyStore policyStore,
        ISctSampleRepository samples,
        ISctOperationLogRepository logs,
        IErrorCollector errors,
        TimeProvider? timeProvider = null)
    {
        _policyStore = policyStore;
        _samples = samples;
        _logs = logs;
        _errors = errors;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ==================== 队列 ====================

    /// <inheritdoc />
    public int SubmitTask(string taskType, string? sampleId, string reason) =>
        SubmitTaskCore(taskType, sampleId, reason, isRecheck: false);

    /// <summary>入队核心（isRecheck：超差闭环验证重测标记，供执行编排与记录区分）。</summary>
    private int SubmitTaskCore(string taskType, string? sampleId, string reason, bool isRecheck)
    {
        lock (_gate)
        {
            // 去重合并：同类型同目标的未完成任务只保留一个（引擎设计 §6.2）
            var existing = _queue.FirstOrDefault(t => t.TaskType == taskType && t.SampleId == sampleId);
            if (existing is not null)
            {
                return existing.TaskId;
            }

            var task = new SctTask
            {
                TaskId = ++_nextTaskId,
                TaskType = taskType,
                SampleId = sampleId,
                Reason = reason,
                EnqueueTime = _time.GetLocalNow().DateTime,
                Priority = GetPriority(taskType),
                IsRecheck = isRecheck,
            };
            _queue.Add(task);
            WriteLog(taskType, sampleId, reason, resultStatus: "Success", isPass: null);
            return task.TaskId;
        }
    }

    /// <inheritdoc />
    public bool TryDequeue(out SctTask task)
    {
        lock (_gate)
        {
            task = null!;
            if (_running is not null || _queue.Count == 0)
            {
                return false;   // 同一时刻仅一个任务 Running（互斥）
            }

            task = _queue
                .OrderBy(t => t.Priority)               // P29 优先级序
                .ThenBy(t => t.EnqueueTime)             // 同优先级 FIFO
                .First();
            _queue.Remove(task);
            task.Status = SctTaskStatus.Running;
            _running = task;
            return true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SctTask> GetQueuedTasks() =>
        _queue
            .OrderBy(t => t.Priority)
            .ThenBy(t => t.EnqueueTime)
            .ToList();

    /// <inheritdoc />
    public IReadOnlyList<SctTask> GetRecentTasks()
    {
        var running = _running is not null ? new[] { _running } : Array.Empty<SctTask>();
        return running.Concat(_queue
                .OrderBy(t => t.Priority)
                .ThenBy(t => t.EnqueueTime))
            .Concat(_history)
            .ToList();
    }

    /// <inheritdoc />
    public void CompleteTask(int taskId, bool success, string? error)
    {
        lock (_gate)
        {
            if (_running?.TaskId != taskId)
            {
                return;
            }

            _running.Status = success ? SctTaskStatus.Completed : SctTaskStatus.Failed;
            _history.Insert(0, _running);
            if (_history.Count > 50)
            {
                _history.RemoveAt(_history.Count - 1);
            }
            _running = null;

            // 失败不自动重试（引擎设计 §6.2），错误经统一错误链上报
            if (!success)
            {
                _errors.Collect(ErrorCodes.UnhandledException, $"SCT 任务 {taskId} 执行失败：{error}", null);
            }
        }
    }

    /// <inheritdoc />
    public void EmergencyReset()
    {
        lock (_gate)
        {
            foreach (var t in _queue)
            {
                t.Status = SctTaskStatus.Cancelled;
            }

            _queue.Clear();
            _recheckLoop = null;    // 冻结闭环状态，恢复后由人工重新触发
        }
    }

    /// <inheritdoc />
    public SctControlCountdown GetControlCountdown()
    {
        lock (_gate)
        {
            var policy = _policyStore.Load();
            // ByTime 无上次检查记录时按"已满间隔"返回（到期待触发），避免显示负值/NaN
            var minutesSince = _lastControlCheckTime is null
                ? policy.ControlInsertMinutes
                : (_time.GetLocalNow().DateTime - _lastControlCheckTime.Value).TotalMinutes;
            return new SctControlCountdown
            {
                Mode = policy.ControlInsertMode,
                ProductionSinceControl = _productionSinceControl,
                ControlInsertCount = policy.ControlInsertCount,
                MinutesSinceControl = minutesSince,
                ControlInsertMinutes = policy.ControlInsertMinutes,
            };
        }
    }

    // ==================== 事件入口 ====================

    /// <inheritdoc />
    public void NotifyAnalysisCompleted(string sampleId)
    {
        var sample = _samples.GetById(sampleId);
        if (sample is null)
        {
            return;
        }

        var policy = _policyStore.Load();
        var now = _time.GetLocalNow().DateTime;

        lock (_gate)
        {
            if (sample.SampleType == SampleType.Control)
            {
                _lastControlCheckTime = now;    // 判定结果另行经 NotifyControlResult 回喂
                return;
            }

            if (sample.SampleType == SampleType.SettingUp)
            {
                _lastStandardizeTime = now;
            }

            if (sample.SampleType == SampleType.TypeStd)
            {
                _lastTypeStdTime = now;
            }

            if (sample.SampleType != SampleType.Production)
            {
                CheckUsageThreshold(sample);
                return;
            }

            // 生产样完成：按数量插入检查样（引擎设计 §5）
            if (policy.ControlInsertMode == ControlInsertMode.ByCount)
            {
                _productionSinceControl++;
                if (_productionSinceControl >= policy.ControlInsertCount)
                {
                    _productionSinceControl = 0;
                    EnqueueWithSelection(SctTaskType.ControlCheck, "按分析数量插入");
                }
            }
        }
    }

    /// <inheritdoc />
    public void NotifyControlResult(string sampleId, bool passed)
    {
        var policy = _policyStore.Load();
        WriteLog(SctTaskType.ControlCheck, sampleId,
            _recheckLoop is not null ? "超差验证重测" : "检查样检查",
            resultStatus: passed ? "Success" : "Fail", isPass: passed);

        lock (_gate)
        {
            if (passed)
            {
                // 通过：闭环结束（若激活），恢复生产（引擎设计 §7）
                _recheckLoop = null;
                _productionSinceControl = 0;
                _lastControlCheckTime = _time.GetLocalNow().DateTime;
                return;
            }

            // 超差：按配置走闭环或直接报警（引擎设计 §7）
            if (!policy.AutoStandardizeOnControlFail)
            {
                _errors.Collect(ErrorCodes.SctRecheckExhausted,
                    "检查样超差且未启用自动标准化，需人工处理", sampleId);
                return;
            }

            if (_recheckLoop is null)
            {
                // 首次超差：激活闭环 → 标准化（优先级 1）→ 重测同一检查样验证
                _recheckLoop = new RecheckLoopState { ControlSampleId = sampleId, RecheckCount = 0 };
                EnqueueStandardize("检查样超差");
                return;
            }

            // 验证重测仍超差：重测次数 < ControlRecheckMaxCount → 再标准化循环；耗尽 → 报警暂停自动模式（引擎设计 §7）
            _recheckLoop.RecheckCount++;
            if (_recheckLoop.RecheckCount >= policy.ControlRecheckMaxCount)
            {
                _errors.Collect(ErrorCodes.SctRecheckExhausted,
                    $"检查样 {sampleId} 超差闭环重测 {_recheckLoop.RecheckCount} 次仍超差，暂停自动模式", sampleId);
                _recheckLoop = null;
                return;
            }

            EnqueueStandardize("检查样超差");
        }
    }

    /// <inheritdoc />
    public void NotifyStandardizeCompleted(string sampleId)
    {
        lock (_gate)
        {
            _lastStandardizeTime = _time.GetLocalNow().DateTime;

            // 闭环推进：标准化完成 → 重测同一检查样验证（引擎设计 §7）
            if (_recheckLoop is not null)
            {
                SubmitTaskCore(SctTaskType.ControlCheck, _recheckLoop.ControlSampleId, "超差验证重测", isRecheck: true);
            }
        }
    }

    /// <inheritdoc />
    public void NotifyHeightMeasured(string sampleId, double thickness)
    {
        var sample = _samples.GetById(sampleId);
        if (sample is null)
        {
            return;
        }

        var before = sample.CurrentThickness;
        var now = _time.GetLocalNow().DateTime;

        // 厚度流转（与测高登记同一状态机，引擎设计 §8）
        if (thickness < sample.MinThickness)
        {
            sample.CurrentThickness = thickness;
            sample.Status = SampleStatus.Disabled;
            _samples.Update(sample);
            WriteLog(SctTaskType.HeightMeasure, sampleId, "测高回填",
                resultStatus: "Fail", isPass: null, thicknessBefore: before, thicknessAfter: thickness);
            _errors.Collect(ErrorCodes.SctThicknessBelowMinimum,
                $"标样 {sampleId} 测高后厚度 {thickness:0.##} 低于最小厚度 {sample.MinThickness:0.##}，请更换", sampleId);
            return;
        }

        sample.CurrentThickness = thickness;
        sample.Status = SampleStatus.Normal;    // 清 NeedMeasure
        sample.LastHeightCheckTime = now;
        _samples.Update(sample);
        WriteLog(SctTaskType.HeightMeasure, sampleId, "测高回填",
            resultStatus: "Success", isPass: null, thicknessBefore: before, thicknessAfter: thickness);
    }

    /// <inheritdoc />
    public void NotifyModeChanged(bool autoMode)
    {
        lock (_gate)
        {
            var wasAuto = _isAutoMode;
            _isAutoMode = autoMode;
            if (!autoMode || wasAuto)
            {
                return;     // 仅"切入自动模式"边沿触发
            }
        }

        var policy = _policyStore.Load();
        if (policy.StandardizeOnAutoMode)
        {
            lock (_gate)
            {
                EnqueueStandardize("切换自动模式");
            }
        }
    }

    /// <inheritdoc />
    public void NotifyAlloySwitched(string alloyType)
    {
        _currentAlloy = alloyType;
        var policy = _policyStore.Load();
        if (policy.TypeStandardizeOnAlloySwitch)
        {
            lock (_gate)
            {
                EnqueueWithSelection(SctTaskType.TypeStandardize, "合金类型切换", alloyType);
            }
        }
    }

    /// <inheritdoc />
    public void OnTick()
    {
        var policy = _policyStore.Load();
        var now = _time.GetLocalNow().DateTime;

        lock (_gate)
        {
            // 定时标准化（引擎设计 §5；上次时间未知视为已到期）
            if (policy.PeriodicStandardizeEnabled &&
                (_lastStandardizeTime is null || now - _lastStandardizeTime >= TimeSpan.FromHours(policy.PeriodicStandardizeHours)))
            {
                EnqueueStandardize("定时标准化");
            }

            // 按时间间隔插入检查样
            if (policy.ControlInsertMode == ControlInsertMode.ByTime &&
                (_lastControlCheckTime is null || now - _lastControlCheckTime >= TimeSpan.FromMinutes(policy.ControlInsertMinutes)))
            {
                EnqueueWithSelection(SctTaskType.ControlCheck, "按时间间隔插入");
            }

            // 类型标准化期限届满
            if (policy.TypeStandardizePeriodEnabled &&
                (_lastTypeStdTime is null || now - _lastTypeStdTime >= TimeSpan.FromDays(policy.TypeStandardizePeriodDays)))
            {
                EnqueueWithSelection(SctTaskType.TypeStandardize, "类型标准化期限届满", _currentAlloy);
            }
        }
    }

    // ==================== 选样与闭环内部逻辑 ====================

    /// <summary>入队标准化任务：选样失败（无可用控样）→ 5101 报警不入队（任务清单第 5 节②）。</summary>
    private void EnqueueStandardize(string reason)
    {
        var policy = _policyStore.Load();
        var candidate = SelectStandardSample(policy.SettingSampleSelectionMode);
        if (candidate is null)
        {
            _errors.Collect(ErrorCodes.SctSettingSampleUnavailable, "无可用控样（须 SettingUp/Normal/启用），暂停分析", null);
            WriteLog(SctTaskType.Standardize, null, reason, resultStatus: "Fail", isPass: null);
            return;
        }

        SubmitTask(SctTaskType.Standardize, candidate.SampleId, reason);
    }

    /// <summary>入队控制检查/类型标准化任务：带合金匹配与无可用报警（5103）。</summary>
    private void EnqueueWithSelection(string taskType, string reason, string? alloyType = null)
    {
        if (taskType == SctTaskType.ControlCheck)
        {
            var policy = _policyStore.Load();
            var control = SelectControlSample(policy);
            if (control is null)
            {
                _errors.Collect(ErrorCodes.SctSettingSampleUnavailable, "无可用检查样（须 Control/Normal/启用），暂停分析", null);
                WriteLog(taskType, null, reason, resultStatus: "Fail", isPass: null);
                return;
            }

            SubmitTask(taskType, control.SampleId, reason);
            return;
        }

        // 类型标准化：AlloyType 精确匹配目标合金（任务清单第 5 节⑤）
        var typeStd = SelectTypeStdSample(alloyType);
        if (typeStd is null)
        {
            _errors.Collect(ErrorCodes.SctTypeStdUnavailable,
                $"合金 {alloyType ?? "未知"} 无匹配的类型标准化样", null);
            WriteLog(taskType, null, reason, resultStatus: "Fail", isPass: null);
            return;
        }

        SubmitTask(taskType, typeStd.SampleId, reason);
    }

    /// <summary>
    /// 控样选择（任务清单第 5 节②）：SettingUp/Normal/IsActive；
    /// LeastUsed=使用次数最少；RoundRobin=上次使用时间最早（未用过的优先，实现轮询）。
    /// </summary>
    private SctSample? SelectStandardSample(string mode)
    {
        var candidates = _samples.GetAll()
            .Where(s => s.SampleType == SampleType.SettingUp && s.Status == SampleStatus.Normal && s.IsActive)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        return mode == SettingSampleSelectionMode.RoundRobin
            ? candidates.OrderBy(s => s.LastUseTime ?? DateTime.MinValue).First()
            : candidates.OrderBy(s => s.UsageCount).First();
    }

    /// <summary>
    /// 检查样选择（任务清单第 5 节③）：Control/Normal/IsActive；
    /// 轮换 = 上次使用时间最早（多块轮换）。检查样不关联合金，不做合金过滤。
    /// </summary>
    private SctSample? SelectControlSample(SctPolicyOptions policy)
    {
        return _samples.GetAll()
            .Where(s => s.SampleType == SampleType.Control && s.Status == SampleStatus.Normal && s.IsActive)
            .OrderBy(s => s.LastUseTime ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    /// <summary>类型标准化样选择（任务清单第 5 节⑤）：TypeStd/Normal/IsActive 且合金精确匹配。</summary>
    private SctSample? SelectTypeStdSample(string? alloyType) =>
        _samples.GetAll()
            .Where(s => s.SampleType == SampleType.TypeStd && s.Status == SampleStatus.Normal && s.IsActive)
            .Where(s => alloyType is null || s.AlloyType == alloyType)
            .OrderBy(s => s.UsageCount)
            .FirstOrDefault();

    /// <summary>
    /// 使用次数阈值联动（引擎设计 §8）：激发后 UsageCount ≥ MaxUsageCount → 置 NeedMeasure 并插测高任务。
    /// 由 Workflow 在激发落库（UpdateAfterSpark）后调用本方法前保证档案已更新。
    /// </summary>
    private void CheckUsageThreshold(SctSample sample)
    {
        if (sample.Status == SampleStatus.Normal && sample.UsageCount >= sample.MaxUsageCount)
        {
            sample.Status = SampleStatus.NeedMeasure;
            _samples.Update(sample);
            WriteLog(SctTaskType.HeightMeasure, sample.SampleId, "使用次数达到上限",
                resultStatus: "Warning", isPass: null);
            SubmitTask(SctTaskType.HeightMeasure, sample.SampleId, "使用次数达到上限，先测高");
        }
    }

    /// <summary>P29 固定优先级映射（引擎设计 §6.1；手动标准化视同超差档，保证高于生产分析）。</summary>
    private static int GetPriority(string taskType) => taskType switch
    {
        SctTaskType.Standardize => PriorityStandardize,
        SctTaskType.ControlCheck => PriorityControlCheck,
        SctTaskType.TypeStandardize => PriorityTypeStandardize,
        SctTaskType.HeightMeasure => PriorityHeightMeasure,
        SctTaskType.Production => PriorityProduction,
        SctTaskType.Archive => PriorityArchive,
        _ => PriorityProduction,
    };

    /// <summary>写执行记录（审计留痕；任务清单第 12 节）。</summary>
    private void WriteLog(string operationType, string? sampleId, string reason,
        string resultStatus, bool? isPass, double? thicknessBefore = null, double? thicknessAfter = null)
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
                IsPass = isPass,
                ThicknessBefore = thicknessBefore,
                ThicknessAfter = thicknessAfter,
                OperationTime = _time.GetLocalNow().DateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                ResultStatus = resultStatus,
            });
        }
        catch (Exception ex)
        {
            // 日志写库失败不阻断决策主流程，但必须进错误链（禁止吞异常）
            _errors.Collect(ErrorCodes.SctSampleMaintainFailed, "SCT 策略引擎写执行记录失败", sampleId, ex);
        }
    }
}
