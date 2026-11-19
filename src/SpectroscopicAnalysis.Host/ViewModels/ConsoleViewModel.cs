using CommunityToolkit.Mvvm.ComponentModel;
using SpectroscopicAnalysis.Host.LoggingAlarm;

namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>
/// 主控台 ViewModel：承载报警面板（当前为静态示例卡，随 LoggingAlarm 数据源落地后接真实数据）。
/// 运行日志模块已迁移至生产监控页（ProductionMonitorViewModel）。
/// </summary>
public sealed partial class ConsoleViewModel : ObservableObject, IPageLifecycle
{
    private readonly IErrorCollector _errors;

    public ConsoleViewModel(IErrorCollector errors)
    {
        _errors = errors;
    }

    /// <summary>导航进入时加载（当前页无动态数据，占位）。</summary>
    public void OnNavigatedTo()
    {
    }
}
