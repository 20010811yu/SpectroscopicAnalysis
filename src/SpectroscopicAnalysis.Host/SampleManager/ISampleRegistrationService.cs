using SpectroscopicAnalysis.Host.Data.Repositories;

namespace SpectroscopicAnalysis.Host.SampleManager;

/// <summary>
/// 样品注册服务抽象（任务清单"入库初始化"：校验 → 查重 → 入库 → 自动入队）。
/// 供样品档案页与主控台等入口共用；UI 层仅依赖本接口（MVVM 分层）。
/// </summary>
public interface ISampleRegistrationService
{
    /// <summary>
    /// 注册样品：表单校验 → 编号/架位查重 → Insert（事务由仓储保证）→ 策略引擎自动入队。
    /// 成功返回 null；失败返回就地展示的错误提示（已同步采集错误链 5003/5004）。
    /// </summary>
    Task<string?> RegisterAsync(SampleRegisterForm form);
}
