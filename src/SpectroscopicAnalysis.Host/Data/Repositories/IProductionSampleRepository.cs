using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>生产样品档案仓储（Production_Sample + Analysis_Result；keyset 游标分页，每次仅取当页）。</summary>
public interface IProductionSampleRepository
{
    /// <summary>生产样游标分页：按入库日期过滤，入库时间倒序（最新在前），每次仅取 pageSize 条。
    /// afterSampleId / beforeSampleId 为游标行 SampleID（复合时间游标），二选一，均空取第一页。</summary>
    IReadOnlyList<ProductionSample> GetPage(DateTime? createdOn, string? afterSampleId, string? beforeSampleId, int pageSize);

    /// <summary>符合条件的生产样总数（分页页数计算）。</summary>
    int Count(DateTime? createdOn);

    /// <summary>全量生产样（样品档案页台账缓存用；按 SampleID 升序）。</summary>
    IReadOnlyList<ProductionSample> GetAll();

    /// <summary>全量分析结果（按 OperationTime 倒序）。</summary>
    IReadOnlyList<AnalysisResult> GetAllResults();

    /// <summary>插入生产样（种子/流程入库）。</summary>
    void Insert(ProductionSample sample);

    /// <summary>插入分析结果。</summary>
    void InsertResult(AnalysisResult result);

    /// <summary>按样品查询分析结果（时间倒序）。</summary>
    IReadOnlyList<AnalysisResult> GetResults(string sampleId);

    /// <summary>事务级联删除：生产样档案 + 其全部分析结果。</summary>
    void DeleteWithResults(string sampleId);
}
