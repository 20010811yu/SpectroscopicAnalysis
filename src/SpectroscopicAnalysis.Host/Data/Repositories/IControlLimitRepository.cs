using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>控制限仓储（查询 + 删除；新增/编辑随控制限管理功能接入）。
/// 前置条件：控制限仅允许关联控制样（SampleType='Control'）——录入/编辑前必须校验样品类型
///（数据库触发器 trg_ControlLimit_ControlOnly 兜底拒绝，见 DbInitializer）。</summary>
public interface IControlLimitRepository
{
    IReadOnlyList<ControlLimit> GetAll();

    /// <summary>按主键删除单条控制限。</summary>
    void Delete(long id);
}
