namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>支持生产日志跳转时间匹配高亮的行模型（RawTime 参与最近匹配，IsLocateHighlighted 驱动视图着色）。</summary>
public interface ILocateHighlightRow
{
    /// <summary>原始操作时间（生产日志跳转的时间匹配用；不绑定 UI）。</summary>
    DateTime? RawTime { get; }

    /// <summary>是否为生产日志跳转的时间匹配行（视图 DataTrigger 高亮底色）。</summary>
    bool IsLocateHighlighted { get; set; }
}

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
    /// <summary>合金类型展示（样品档案查询；无档案或未填显示 —，生产日志列用）。</summary>
    public string AlloyTypeText { get; init; } = "—";
    public string TriggerReasonText { get; init; } = "—";
    /// <summary>开始时间展示（生产样=注册提交时刻，SCT 样=指令下发时刻；未采集/历史行显示 —）。</summary>
    public string StartTimeText { get; init; } = "—";
    /// <summary>完成时间展示（全部流程执行完成后的记录时刻）。</summary>
    public string OperationTimeText { get; init; } = string.Empty;
    public string ResultStatusText { get; init; } = string.Empty;
    /// <summary>原始操作时间串（日期过滤/排序用；不绑定 UI）。</summary>
    public string RawOperationTime { get; init; } = string.Empty;
}


/// <summary>报警行（主控台「报警面板」，来源 ErrorCollector 内存报警表快照，最新在前）。</summary>
public sealed class AlarmRowItem
{
    public long AlarmId { get; init; }
    public string TimeText { get; init; } = string.Empty;
    public string ErrorCodeText { get; init; } = string.Empty;
    public string MessageText { get; init; } = string.Empty;
    /// <summary>样品上下文（无关联显示 —；仅 ToolTip 用）。</summary>
    public string SampleIdText { get; init; } = "—";
    /// <summary>整行文案（时间/错误码/消息在 VM 拼接；有样品上下文才追加「（样品 …）」，无样品报警不显示空占位）。</summary>
    public string DisplayText { get; init; } = string.Empty;
    /// <summary>是否已确认（视图淡化徽章并隐藏确认按钮）。</summary>
    public bool IsAcknowledged { get; init; }
}

/// <summary>全流程日志行（主控台「运行日志」面板，来源 SystemLogCollector 快照，最新在前）。</summary>
public sealed class SystemLogRowItem
{
    public string TimeText { get; init; } = string.Empty;
    /// <summary>类目（应用/PLC连接/VM方案加载/设备操作/报警 等）。</summary>
    public string CategoryText { get; init; } = string.Empty;
    public string MessageText { get; init; } = string.Empty;
    /// <summary>等级（Info/Warning/Failed；视图按等级着色）。</summary>
    public string Level { get; init; } = "Info";
}

/// <summary>货架示意库位（主控台「货架示意」模块；5 行×5 列）。颜色按样品状态区分。</summary>
public sealed class RackSlotItem
{
    /// <summary>库位状态：Empty（灰）/ Normal（蓝=可用）/ NeedMeasure（黄）/ Disabled（红）/ ManualDisabled（橙=人工禁用）。</summary>
    public const string StateEmpty = "Empty";
    public const string StateNormal = "Normal";
    public const string StateNeedMeasure = "NeedMeasure";
    public const string StateDisabled = "Disabled";
    public const string StateManualDisabled = "ManualDisabled";

    /// <summary>架位编号（A01…E08）。</summary>
    public string PositionCode { get; init; } = string.Empty;
    /// <summary>占用样品编号（空闲为空串）。</summary>
    public string SampleIdText { get; init; } = string.Empty;
    /// <summary>是否被样品占用（占位计数用）。</summary>
    public bool IsOccupied { get; init; }
    /// <summary>库位状态（驱动格子底色）。</summary>
    public string SlotState { get; init; } = StateEmpty;
    /// <summary>状态中文（ToolTip 用）。</summary>
    public string StateText { get; init; } = "空位";
    /// <summary>ToolTip 完整文案（占用=样品号+状态中文；空位=「空位」）。</summary>
    public string ToolTipText { get; init; } = "空位";
}

/// <summary>货架示意行（一行 8 个库位）。</summary>
public sealed class RackRowItem
{
    /// <summary>行标识（A–E）。</summary>
    public string RowLabel { get; init; } = string.Empty;
    public List<RackSlotItem> Slots { get; init; } = new();
}