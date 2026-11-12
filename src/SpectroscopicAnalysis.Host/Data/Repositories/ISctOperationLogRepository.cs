using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>SCT 操作日志仓储（查询 + 写入审计留痕 + 删除；删除为演示库维护能力，破坏审计留痕，调用方需二次确认）。</summary>
public interface ISctOperationLogRepository
{
    IReadOnlyList<SctOperationLog> GetAll();

    /// <summary>插入一条操作日志（审计留痕：测高登记/标准化/控制检查等 SCT 操作）。</summary>
    void Insert(SctOperationLog log);

    /// <summary>按主键删除单条操作日志。</summary>
    void Delete(long logId);
}
