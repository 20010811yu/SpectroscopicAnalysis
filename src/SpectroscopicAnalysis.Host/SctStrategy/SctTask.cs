namespace SpectroscopicAnalysis.Host.SctStrategy;

/// <summary>SCT 任务类型（与 CYCLE_START_REQUEST 的 ID_CYCLE 对齐：1 生产 / 2 标准化 / 3 检查样；引擎设计文档 §4）。</summary>
public static class SctTaskType
{
    /// <summary>控样标准化（ID_CYCLE=2）。</summary>
    public const string Standardize = "Standardize";

    /// <summary>检查样检查（ID_CYCLE=3）。</summary>
    public const string ControlCheck = "ControlCheck";

    /// <summary>类型标准化。</summary>
    public const string TypeStandardize = "TypeStandardize";

    /// <summary>测高。</summary>
    public const string HeightMeasure = "HeightMeasure";

    /// <summary>生产样品分析（ID_CYCLE=1）。</summary>
    public const string Production = "Production";

    /// <summary>归档。</summary>
    public const string Archive = "Archive";

    /// <summary>
    /// 注册测高触发原因前缀（新样注册未填初始厚度时入队 HeightMeasure 用）。
    /// 编排器据此在测高合格后按样品类型补入注册分析任务（自动入队闭环）。
    /// </summary>
    public const string ReasonRegistrationMeasure = "新样注册未填初始厚度，先测高确认";
}

/// <summary>SCT 任务状态（引擎设计文档 §4；命名与任务清单第 7 节一致，禁止自创状态名）。</summary>
public static class SctTaskStatus
{
    /// <summary>已入队。</summary>
    public const string Queued = "Queued";

    /// <summary>已出队下发执行。</summary>
    public const string Running = "Running";

    /// <summary>执行完成。</summary>
    public const string Completed = "Completed";

    /// <summary>执行失败（不自动重试）。</summary>
    public const string Failed = "Failed";

    /// <summary>被更高优先级/急停取消。</summary>
    public const string Cancelled = "Cancelled";
}

/// <summary>SCT 任务（引擎出队后由 Workflow 执行；纯数据对象）。</summary>
public sealed class SctTask
{
    /// <summary>任务编号（引擎内自增）。</summary>
    public int TaskId { get; init; }

    /// <summary>任务类型（SctTaskType 常量）。</summary>
    public string TaskType { get; init; } = string.Empty;

    /// <summary>目标样品编号（生产任务为生产样编号；可空）。</summary>
    public string? SampleId { get; init; }

    /// <summary>触发原因（写入执行记录 TriggerReason）。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>入队时间。</summary>
    public DateTime EnqueueTime { get; init; }

    /// <summary>优先级档位（P29 固定次序，数值小者先执行；引擎设计文档 §6.1）。</summary>
    public int Priority { get; init; }

    /// <summary>手动置顶排序键（同档位内生效；null=未手动调整，按入队时间 FIFO。取值=同档最小键-1 秒，多次置顶按点击逆序）。</summary>
    public DateTime? ManualSortKey { get; set; }

    /// <summary>任务状态（SctTaskStatus 常量）。</summary>
    public string Status { get; set; } = SctTaskStatus.Queued;

    /// <summary>超差闭环验证重测标记（ControlCheck 任务专用）。</summary>
    public bool IsRecheck { get; init; }
}
