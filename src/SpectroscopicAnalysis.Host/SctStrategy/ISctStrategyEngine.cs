namespace SpectroscopicAnalysis.Host.SctStrategy;

/// <summary>
/// SCT 策略引擎（引擎设计文档 §10 草案落地）：纯决策——触发判断、样品选择、优先级队列、
/// 超差闭环与测高联动；不做设备 I/O，设备调用由 Workflow 编排（Workflow 编排设计文档 §6）。
/// 依赖抽象仓储与 SctPolicyOptions，可注入 Fake 完整单测。
/// </summary>
public interface ISctStrategyEngine
{
    /// <summary>提交任务（手动触发挂点 / 外部请求）；重复目标同类型未完成任务去重合并，返回 TaskId。</summary>
    int SubmitTask(string taskType, string? sampleId, string reason);

    /// <summary>尝试出队下一个可执行任务（优先级序，同优先级 FIFO；同时仅一个任务 Running）。</summary>
    bool TryDequeue(out SctTask task);

    /// <summary>手动调整优先级：把排队任务在本优先级档内置顶（人工调整仅同档生效，不越过更高档任务；执行中任务不可调整）。成功返回 true。</summary>
    bool MoveToTop(int taskId);

    /// <summary>删除单条排队任务（仅 Queued 可删；置 Cancelled 留痕历史，不影响样品档案与数据）。成功返回 true。</summary>
    bool RemoveQueuedTask(int taskId);

    /// <summary>移除指定样品的全部排队任务（样品档案删除时联动调用；仅 Queued 受影响）。返回移除条数。</summary>
    int RemoveQueuedTasksBySample(string sampleId);

    /// <summary>当前排队任务快照（优先级序，同优先级 FIFO；与 TryDequeue 取序一致，供队列面板展示）。</summary>
    IReadOnlyList<SctTask> GetQueuedTasks();

    /// <summary>最近任务快照（执行中 + 排队 + 已终结历史最新在前；供队列面板展示，任务不再凭空消失）。</summary>
    IReadOnlyList<SctTask> GetRecentTasks();

    /// <summary>任务完成/失败回执（Workflow 执行后回调；失败不自动重试）。</summary>
    void CompleteTask(int taskId, bool success, string? error);

    /// <summary>样品分析完成（生产样完成计数→检查样插入；标样次数联动→测高任务，引擎设计 §5/§8）。
    /// taskType：触发本次分析的任务类型（混用时计时按任务用途归属——ControlCheck 刷检查计时、TypeStandardize 刷类型标准化期限），
    /// 为 null 时按样品档案类型归属（向后兼容）。</summary>
    void NotifyAnalysisCompleted(string sampleId, string? taskType = null);

    /// <summary>检查样判定结果（超差闭环推进，引擎设计 §7；passed 按 LimitType/LimitValue 判定后的布尔结果）。</summary>
    void NotifyControlResult(string sampleId, bool passed);

    /// <summary>标准化完成（超差闭环推进：安排验证重测，引擎设计 §7）。</summary>
    void NotifyStandardizeCompleted(string sampleId);

    /// <summary>测高结果回填（厚度流转：低于最小厚度→Disabled+报警；否则→Normal，引擎设计 §8）。</summary>
    void NotifyHeightMeasured(string sampleId, double thickness);

    /// <summary>系统模式切换（切入自动模式按配置触发标准化，引擎设计 §5）。</summary>
    void NotifyModeChanged(bool autoMode);

    /// <summary>合金类型切换（按配置触发类型标准化，引擎设计 §5）。</summary>
    void NotifyAlloySwitched(string alloyType);

    /// <summary>仅同步当前合金（不触发类型标准化入队；Workflow 生产流程前置检测合金切换后调用，供期限届满等判定使用）。</summary>
    void UpdateCurrentAlloy(string alloyType);

    /// <summary>时钟节拍（定时标准化/按时间插入检查样/类型标准化期限判定；由宿主定期调用）。</summary>
    void OnTick();

    /// <summary>急停/故障恢复：清空队列（全部 Cancelled），冻结闭环状态。</summary>
    void EmergencyReset();

    /// <summary>检查样插入进度快照（模式/已计数或已过分钟/阈值；主控台 KPI 展示）。</summary>
    SctControlCountdown GetControlCountdown();

    /// <summary>定期标准化与类型标准化期限倒计时快照（到期时刻；生产监控 KPI 展示；开关关闭或无记录返回 null 到期时刻）。</summary>
    SctStandardizeCountdown GetStandardizeCountdown();
}
