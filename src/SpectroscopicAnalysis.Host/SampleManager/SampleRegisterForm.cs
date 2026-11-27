namespace SpectroscopicAnalysis.Host.SampleManager;

/// <summary>
/// 样品注册表单数据（纯数据，与 UI 无关；字符串保留原始输入，解析/校验在服务内）。
/// 字段与样品档案页注册面板一致（SamplesViewModel Reg* 字段）。
/// </summary>
public sealed class SampleRegisterForm
{
    /// <summary>样品类型键（SampleType.Production/SettingUp/Control/TypeStd）。</summary>
    public string SampleType { get; set; } = Data.Models.SampleType.Production;

    /// <summary>样品编号（手输，全库唯一）。</summary>
    public string SampleId { get; set; } = string.Empty;

    /// <summary>样品名称。</summary>
    public string SampleName { get; set; } = string.Empty;

    /// <summary>分析方法（生产样必选）。</summary>
    public string? AnalysisMethod { get; set; }

    /// <summary>关联合金（生产样/类型标准化样/勾选兼任的检查样必填；其余禁填）。</summary>
    public string? AlloyType { get; set; }

    /// <summary>检查样兼任类型标准化样（true 时同事务创建 TypeStd 孪生档案：编号 -T 后缀、同架位、互指 PairSampleId）。</summary>
    public bool RegisterAsTypeStdTwin { get; set; }

    /// <summary>货架位置（SCT 样必填，全库唯一）。</summary>
    public string? RackPosition { get; set; }

    /// <summary>初始厚度文本（SCT 样非必填；填了须 >0 且 > 最小厚度，留空则注册后强制先测高）。</summary>
    public string InitialThickness { get; set; } = string.Empty;

    /// <summary>最小厚度文本（SCT 样必填，>0）。</summary>
    public string MinThickness { get; set; } = string.Empty;

    /// <summary>最大使用次数文本（SCT 样必填，≥1）。</summary>
    public string MaxUsageCount { get; set; } = string.Empty;
}
