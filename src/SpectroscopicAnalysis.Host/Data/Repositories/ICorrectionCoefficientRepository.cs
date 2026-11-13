using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>校正系数仓储（查询 + 删除；写入随校正计算/标准化流程接入）。
/// 前置条件：SourceSampleID 仅允许设置样/类型标准化样（SampleType IN ('SettingUp','TypeStd')，NULL 除外）——
/// 写入前必须校验来源样品类型（数据库触发器 trg_CorrectionCoefficient_SourceSampleType 兜底拒绝，见 DbInitializer）。</summary>
public interface ICorrectionCoefficientRepository
{
    IReadOnlyList<CorrectionCoefficient> GetAll();

    /// <summary>按主键删除单条系数版本（演示库维护用；删除破坏版本化历史，调用方需二次确认）。</summary>
    void Delete(long coeffId);
}
