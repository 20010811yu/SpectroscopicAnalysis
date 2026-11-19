namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>
/// 样品档案页「切换数据表」的 4 张子表行展示模型（领域数据 → 展示文本映射，开发规范 3.3）。
/// 与 SampleRowItem 同模式：数值格式化 / 枚举中文化在 VM 层完成，View 只做绑定。
/// </summary>

/// <summary>标准值行（SCT_CertifiedValue）。</summary>
public sealed class CertifiedValueRowItem
{
    /// <summary>主键（删除命令参数）。</summary>
    public long Id { get; init; }
    public string SampleId { get; init; } = string.Empty;
    public string Element { get; init; } = string.Empty;
    public string ValueText { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public string UncertaintyText { get; init; } = "—";
}

/// <summary>控制限行（ControlLimit）。</summary>
public sealed class ControlLimitRowItem
{
    /// <summary>主键（删除命令参数）。</summary>
    public long LimitId { get; init; }
    public string SampleId { get; init; } = string.Empty;
    public string Element { get; init; } = string.Empty;
    public string UpperText { get; init; } = string.Empty;
    public string LowerText { get; init; } = string.Empty;
    public string LimitTypeText { get; init; } = string.Empty;
}

/// <summary>校正系数行（CorrectionCoefficient，只读）。</summary>
public sealed class CoefficientRowItem
{
    public long CoeffId { get; init; }
    public string MethodId { get; init; } = string.Empty;
    public string Element { get; init; } = string.Empty;
    public string SlopeText { get; init; } = string.Empty;
    public string InterceptText { get; init; } = string.Empty;
    public string EffectiveTimeText { get; init; } = string.Empty;
    public string SourceSampleText { get; init; } = "—";
    public string CurrentText { get; init; } = string.Empty;
    public string ReferenceElementText { get; init; } = "—";
    public string VersionText { get; init; } = string.Empty;
}

/// <summary>SCT 操作日志行（SCT_OperationLog，只读）。</summary>
public sealed class OperationLogRowItem
{
    public long LogId { get; init; }
    public string SampleIdText { get; init; } = "—";
    /// <summary>原始 SampleID（类型筛选关联用；不绑定 UI）。</summary>
    public string RawSampleId { get; init; } = string.Empty;
    public string OperationTypeText { get; init; } = string.Empty;
    public string TriggerReasonText { get; init; } = "—";
    public string OperationTimeText { get; init; } = string.Empty;
    public string ResultStatusText { get; init; } = string.Empty;
    /// <summary>原始操作时间串（日期过滤/排序用；不绑定 UI）。</summary>
    public string RawOperationTime { get; init; } = string.Empty;
}
