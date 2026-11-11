using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>校正系数仓储（查询 + 删除；写入随校正计算/标准化流程接入）。</summary>
public interface ICorrectionCoefficientRepository
{
    IReadOnlyList<CorrectionCoefficient> GetAll();

    /// <summary>按主键删除单条系数版本（演示库维护用；删除破坏版本化历史，调用方需二次确认）。</summary>
    void Delete(long coeffId);
}
