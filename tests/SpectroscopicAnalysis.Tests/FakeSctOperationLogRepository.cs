using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;

namespace SpectroscopicAnalysis.Tests;

/// <summary>Fake 操作日志仓储：内存模拟（GetAll 按操作时间倒序，与真实 SQL 等价）。供主控台测试共用。</summary>
internal sealed class FakeSctOperationLogRepository : ISctOperationLogRepository
{
    public List<SctOperationLog> Store { get; } = new();

    public IReadOnlyList<SctOperationLog> GetAll() =>
        Store.OrderByDescending(l => l.OperationTime, StringComparer.Ordinal).ToList();

    public IReadOnlyList<SctOperationLog> GetRecentBySampleId(string sampleId, int limit) =>
        Store.Where(l => l.SampleId == sampleId)
            .OrderByDescending(l => l.OperationTime, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

    public void Insert(SctOperationLog log) => Store.Add(log);

    public void Delete(long logId) => Store.RemoveAll(l => l.LogId == logId);
}
