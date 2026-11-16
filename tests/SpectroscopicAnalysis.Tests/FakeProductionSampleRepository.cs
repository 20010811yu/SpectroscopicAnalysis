using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;

namespace SpectroscopicAnalysis.Tests;

/// <summary>Fake 生产样仓储：内存模拟 keyset 游标分页语义（与真实 SQL DESC LIMIT 后反转等价）。供 Dashboard 测试共用。</summary>
internal sealed class FakeProductionSampleRepository : IProductionSampleRepository
{
    public List<ProductionSample> Store { get; } = new();

    public List<AnalysisResult> Results { get; } = new();

    public IReadOnlyList<ProductionSample> GetPage(DateTime? createdOn, string? afterSampleId, string? beforeSampleId, int pageSize)
    {
        var query = Store.AsEnumerable();
        if (createdOn is not null)
        {
            var day = createdOn.Value.Date;
            query = query.Where(s => s.CreatedTime >= day && s.CreatedTime < day.AddDays(1));
        }

        // 与真实 SQL 一致：入库时间倒序（最新在前），(CreatedTime, SampleID) 复合游标防重漏
        var anchorId = afterSampleId ?? beforeSampleId;
        var anchorTime = anchorId is null
            ? null
            : Store.FirstOrDefault(s => s.SampleId == anchorId)?.CreatedTime;
        var backward = beforeSampleId is not null;

        if (afterSampleId is not null)
        {
            query = query.Where(s => s.CreatedTime < anchorTime
                || (s.CreatedTime == anchorTime && string.CompareOrdinal(s.SampleId, afterSampleId) < 0));
        }

        if (beforeSampleId is not null)
        {
            query = query.Where(s => s.CreatedTime > anchorTime
                || (s.CreatedTime == anchorTime && string.CompareOrdinal(s.SampleId, beforeSampleId) > 0));
        }

        var ordered = query.OrderByDescending(s => s.CreatedTime)
            .ThenByDescending(s => s.SampleId, StringComparer.Ordinal);
        if (backward)
        {
            // 与真实 SQL（ASC LIMIT 后反转）等价：游标之后（更新）的相邻一页，保持倒序返回
            return ordered.TakeLast(pageSize).ToList();
        }

        return ordered.Take(pageSize).ToList();
    }

    public int Count(DateTime? createdOn)
    {
        var query = Store.AsEnumerable();
        if (createdOn is not null)
        {
            var day = createdOn.Value.Date;
            query = query.Where(s => s.CreatedTime >= day && s.CreatedTime < day.AddDays(1));
        }

        return query.Count();
    }

    public void Insert(ProductionSample sample) => Store.Add(sample);

    public void InsertResult(AnalysisResult result) => Results.Add(result);

    public IReadOnlyList<AnalysisResult> GetResults(string sampleId) =>
        Results.Where(r => r.SampleId == sampleId).OrderByDescending(r => r.OperationTime).ToList();

    public IReadOnlyList<ProductionSample> GetAll() =>
        Store.OrderByDescending(s => s.CreatedTime)
             .ThenBy(s => s.SampleId, StringComparer.Ordinal)
             .ToList();

    public IReadOnlyList<AnalysisResult> GetAllResults() =>
        Results.OrderByDescending(r => r.OperationTime).ThenByDescending(r => r.ResultId).ToList();

    public void DeleteWithResults(string sampleId)
    {
        Store.RemoveAll(s => s.SampleId == sampleId);
        Results.RemoveAll(r => r.SampleId == sampleId);
    }
}
