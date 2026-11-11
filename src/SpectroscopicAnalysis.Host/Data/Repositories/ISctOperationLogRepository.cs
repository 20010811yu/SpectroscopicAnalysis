using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>SCT 操作日志仓储（只读查询；审计留痕，禁止删除/修改，规则 3）。</summary>
public interface ISctOperationLogRepository
{
    IReadOnlyList<SctOperationLog> GetAll();
}
