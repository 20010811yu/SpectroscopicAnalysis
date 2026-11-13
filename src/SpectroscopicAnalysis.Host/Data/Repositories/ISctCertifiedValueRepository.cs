using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>标准值仓储（查询 + 删除；新增/编辑随标准值管理功能接入）。</summary>
public interface ISctCertifiedValueRepository
{
    IReadOnlyList<SctCertifiedValue> GetAll();

    /// <summary>按样品查询标准值（元素升序；样品详情面板用）。</summary>
    IReadOnlyList<SctCertifiedValue> GetBySampleId(string sampleId);

    /// <summary>按主键删除单条标准值。</summary>
    void Delete(long id);
}
