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

        var ordered = query.OrderBy(s => s.SampleId, StringComparer.Ordinal);
        if (afterSampleId is not null)
        {
            ordered = ordered.Where(s => string.CompareOrdinal(s.SampleId, afterSampleId) > 0)
                .OrderBy(s => s.SampleId, StringComparer.Ordinal);
        }

        if (beforeSampleId is not null)
        {
            // 与真实 SQL（DESC LIMIT 后反转）等价：游标前最后 pageSize 条，升序返回
            return ordered.Where(s => string.CompareOrdinal(s.SampleId, beforeSampleId) < 0)
                .OrderBy(s => s.SampleId, StringComparer.Ordinal)
                .TakeLast(pageSize)
                .ToList();
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
