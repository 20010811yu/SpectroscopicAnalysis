using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>SCT 样品档案仓储（其余 4 表仓储随 D4 按同一模式补齐）。</summary>
public interface ISctSampleRepository
{
    SctSample? GetById(string sampleId);

    IReadOnlyList<SctSample> GetAll();

    void Insert(SctSample sample);

    void Update(SctSample sample);

    void Delete(string sampleId);

    /// <summary>
    /// 删除样品及其外键子数据（标准值、控制限），单事务执行；
    /// SCT_OperationLog 保留（审计追溯）。样品不存在时静默成功。
    /// </summary>
    void DeleteWithChildren(string sampleId);

    /// <summary>激发完成后持久化使用结果：次数 +1、当前厚度、状态、上次使用时间（事务内更新）。</summary>
    void UpdateAfterSpark(string sampleId, double currentThickness, string status, DateTime usedAt);

    /// <summary>
    /// 生产样游标分页（keyset）：按入库日期过滤，SampleID 升序，每次仅取 pageSize 条（不回表全量）。
    /// afterSampleId（向后翻页游标）/ beforeSampleId（向前翻页游标）二选一，均空取第一页。
    /// </summary>
    IReadOnlyList<SctSample> GetProductionPage(DateTime? createdOn, string? afterSampleId, string? beforeSampleId, int pageSize);

    /// <summary>符合条件的生产样总数（分页页数计算）。</summary>
    int CountProduction(DateTime? createdOn);
}
