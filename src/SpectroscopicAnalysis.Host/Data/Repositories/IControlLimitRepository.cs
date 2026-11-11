using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>控制限仓储（查询 + 删除；新增/编辑随控制限管理功能接入）。</summary>
public interface IControlLimitRepository
{
    IReadOnlyList<ControlLimit> GetAll();

    /// <summary>按主键删除单条控制限。</summary>
    void Delete(long id);
}
