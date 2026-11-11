using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>校正系数仓储（只读查询；写入随校正计算/标准化流程接入，版本化不删除）。</summary>
public interface ICorrectionCoefficientRepository
{
    IReadOnlyList<CorrectionCoefficient> GetAll();
}
