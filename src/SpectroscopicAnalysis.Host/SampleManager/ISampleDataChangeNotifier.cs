namespace SpectroscopicAnalysis.Host.SampleManager;

/// <summary>
/// 样品数据变更通知（跨页面刷新通道）：任一模块写样品档案（维护操作/任务回喂/激发落库）后发布，
/// 订阅方（样品档案/ SCT 管理等常驻页面 VM）收到后重载台账，消除「操作完成后切页才见新数据」。
/// 事件在任意线程触发，订阅方自行经 IUiDispatcher 切回 UI 线程（MVVM 分层）。
/// </summary>
public interface ISampleDataChangeNotifier
{
    /// <summary>样品数据变更事件（无参数；订阅方全量重载各自的样品数据）。</summary>
    event Action? SampleDataChanged;

    /// <summary>发布一次样品数据变更（可从任意线程调用；重入安全，订阅方自行防抖）。</summary>
    void NotifySampleDataChanged();
}

/// <summary>样品数据变更通知默认实现（DI 单例；仅事件转发，无状态）。</summary>
public sealed class SampleDataChangeNotifier : ISampleDataChangeNotifier
{
    public event Action? SampleDataChanged;

    /// <inheritdoc />
    public void NotifySampleDataChanged() => SampleDataChanged?.Invoke();
}
