namespace SpectroscopicAnalysis.Host.Data.Models;

/// <summary>标准值（表 SCT_CertifiedValue，字段见任务清单 2.2；SampleID 外键引用 SCT_Sample）。</summary>
public sealed class SctCertifiedValue
{
    public long Id { get; set; }
    public string SampleId { get; set; } = string.Empty;
    public string Element { get; set; } = string.Empty;
    public double CertifiedValue { get; set; }
    public string Unit { get; set; } = "%";
    public double? Uncertainty { get; set; }
}

/// <summary>控制限（表 ControlLimit，字段见任务清单 2.5；SampleID 外键引用 SCT_Sample）。</summary>
public sealed class ControlLimit
{
    public long LimitId { get; set; }
    public string SampleId { get; set; } = string.Empty;
    public string Element { get; set; } = string.Empty;
    public double UpperLimit { get; set; }
    public double LowerLimit { get; set; }
    /// <summary>限类型：Absolute / Percent / Sigma。</summary>
    public string LimitType { get; set; } = "Sigma";
}

/// <summary>
/// 校正系数（表 CorrectionCoefficient，字段见任务清单 2.3；版本化：IsCurrent + Version）。
/// 注意：不含操作者字段（身份内容已移除，规则 11）；SourceSampleID 为来源控样，可空。
/// </summary>
public sealed class CorrectionCoefficient
{
    public long CoeffId { get; set; }
    public string MethodId { get; set; } = string.Empty;
    public string Element { get; set; } = string.Empty;
    public double Slope { get; set; }
    public double Intercept { get; set; }
    public string EffectiveTime { get; set; } = string.Empty;
    public string? SourceSampleId { get; set; }
    public bool IsCurrent { get; set; }
    public string? ReferenceElement { get; set; }
    public int Version { get; set; } = 1;
}

/// <summary>SCT 操作日志（表 SCT_OperationLog，字段见任务清单 2.4；审计留痕，只读）。</summary>
public sealed class SctOperationLog
{
    public long LogId { get; set; }
    public string? SampleId { get; set; }
    public string OperationType { get; set; } = string.Empty;
    public string? TriggerReason { get; set; }
    public string? RawIntensity { get; set; }
    public string? MeasuredConc { get; set; }
    public string? CorrectedConc { get; set; }
    public bool? IsPass { get; set; }
    public double? ThicknessBefore { get; set; }
    public double? ThicknessAfter { get; set; }
    public string? CoeffBefore { get; set; }
    public string? CoeffAfter { get; set; }
    /// <summary>开始时间（生产样 = 提交样品注册时刻；控样/类型标准化样/检查样 = 激发指令下发时刻；历史行为 NULL）。</summary>
    public string? StartTime { get; set; }
    /// <summary>完成时间（列名 OperationTime，语义为全部流程执行完成后的记录时刻）。</summary>
    public string OperationTime { get; set; } = string.Empty;
    /// <summary>结果状态：Success / Fail / Warning。</summary>
    public string ResultStatus { get; set; } = "Success";
}
