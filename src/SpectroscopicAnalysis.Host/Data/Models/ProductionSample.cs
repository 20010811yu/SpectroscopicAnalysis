namespace SpectroscopicAnalysis.Host.Data.Models;

/// <summary>生产样品分析状态（Production_Sample 专用状态机，与 SCT 样品状态互不影响）。</summary>
public static class ProductionStatus
{
    public const string Normal = "Normal";
    public const string Analyzing = "Analyzing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

/// <summary>分析结果状态（Analysis_Result；与 SCT 操作日志结果状态一致）。</summary>
public static class AnalysisResultStatus
{
    public const string Success = "Success";
    public const string Warning = "Warning";
    public const string Fail = "Fail";
}

/// <summary>生产样品档案（表 Production_Sample，取通用样品表的生产样子集独立建表）。</summary>
public sealed class ProductionSample
{
    public string SampleId { get; set; } = string.Empty;
    public string SampleName { get; set; } = string.Empty;
    public string SampleType { get; set; } = "Production";
    public string? AlloyType { get; set; }
    public string? RackPosition { get; set; }
    public string? AssociatedMethod { get; set; }
    public string Status { get; set; } = ProductionStatus.Normal;
    public DateTime? CreatedTime { get; set; }
    public string? Remark { get; set; }
}

/// <summary>分析结果（表 Analysis_Result，每个生产样 1 条或多条；原始/校正后双存，系数版本可追溯）。</summary>
public sealed class AnalysisResult
{
    public long ResultId { get; set; }
    public string SampleId { get; set; } = string.Empty;
    public string? RawIntensity { get; set; }
    public double? MeasuredConc { get; set; }
    public double? CorrectedConc { get; set; }
    public int? CoeffVersion { get; set; }
    public bool IsPass { get; set; } = true;
    public string ResultStatus { get; set; } = AnalysisResultStatus.Success;
    public string? ArchivePosition { get; set; }
    public DateTime? OperationTime { get; set; }
}
