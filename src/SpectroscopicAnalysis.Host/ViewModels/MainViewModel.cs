using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>侧栏导航项（标题 + 目标 ViewModel + 选中态；Children 非空时为分组项，自身不可导航）。</summary>
public sealed partial class NavItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>分组是否展开（仅 Children 非空时有意义；导航到子项时自动展开）。</summary>
    [ObservableProperty]
    private bool _isExpanded;

    public required string Title { get; init; }

    public required ObservableObject Target { get; init; }

    /// <summary>二级子菜单（null = 普通导航项）。</summary>
    public IReadOnlyList<NavItem>? Children { get; init; }
}

/// <summary>
/// 主窗口 ViewModel：导航切换（含二级菜单分组）+ 缩放档位（界面规范 5.3：100%/125%/150%）。
/// 严格 MVVM：不引用任何 WPF 类型（开发规范 3.3）。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>缩放档位候选（界面规范 5.3）。</summary>
    public static readonly IReadOnlyList<double> ScaleOptions = new[] { 1.0, 1.25, 1.5 };

    [ObservableProperty]
    private ObservableObject? _currentViewModel;

    [ObservableProperty]
    private NavItem? _selectedNav;

    /// <summary>界面缩放系数（1.0/1.25/1.5），根容器 LayoutTransform 绑定。</summary>
    [ObservableProperty]
    private double _uiScale = 1.0;

    /// <summary>顶栏实时时钟文本（顶栏状态区展示；后台定时刷新，MVVM 无 WPF 定时器依赖）。</summary>
    [ObservableProperty]
    private string _clockText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>激光打标机在线状态（设备常驻栏状态点数据源）：EzCad2 进程存活 = 在线，未启动/已退出 = 掉线。</summary>
    [ObservableProperty]
    private bool _laserOnline;

    /// <summary>导航项（静态七项，无需变更通知；首项为二级分组）。</summary>
    public ReadOnlyObservableCollection<NavItem> NavItems { get; }

    public MainViewModel(
        ProductionMonitorViewModel productionMonitor,
        ConsoleViewModel console,
        SamplesViewModel samples,
        SctViewModel sct,
        CoefficientsViewModel coefficients,
        AlarmsViewModel alarms,
        SettingsViewModel settings,
        Vision.IEzCad2Controller ezCad2,
        Ui.IUiDispatcher ui)
    {
        // 二级分组「主控台 · 生产监控」：生产监控（当前任务/KPI/任务队列）+ 主控台（运行日志/报警面板）
        var consoleGroup = new NavItem
        {
            Title = "主控台 · 生产监控",
            Target = productionMonitor,     // 分组头不可点击导航；Target 仅作占位
            Children =
            [
                new() { Title = "主控台", Target = console },
                new() { Title = "生产监控", Target = productionMonitor },
            ],
        };
        var samplesNav = new NavItem { Title = "样品档案", Target = samples };
        var items = new ObservableCollection<NavItem>
        {
            consoleGroup,
            samplesNav,
            new() { Title = "SCT 管理", Target = sct },
            new() { Title = "系数管理", Target = coefficients },
            new() { Title = "日志报警", Target = alarms },
            new() { Title = "设置", Target = settings },
        };
        NavItems = new ReadOnlyObservableCollection<NavItem>(items);

        // 生产监控双击生产日志行 → 切到样品档案页并定位对应样品（编号选中 + 时间就近匹配高亮）
        productionMonitor.SampleLocateRequested += (sampleId, logTime) =>
        {
            NavigateTo(samplesNav);
            _ = samples.LocateSampleAsync(sampleId, logTime);
        };

        // 默认落在主控台
        NavigateTo(consoleGroup.Children.First(c => c.Title == "主控台"));
        StartClock();

        // 激光打标机在线状态：初始取当前值；Exited 事件可能在任意线程，经 IUiDispatcher 切回 UI 线程
        LaserOnline = ezCad2.IsOnline;
        ezCad2.OnlineStateChanged += () => ui.Invoke(() => LaserOnline = ezCad2.IsOnline);
    }

    /// <summary>实时时钟刷新周期（秒；展示精度为分钟，1 秒节拍保证跨分即刻更新）。</summary>
    private static readonly TimeSpan ClockInterval = TimeSpan.FromSeconds(1);

    /// <summary>启动顶栏实时时钟循环（主窗口为单例常驻，随宿主退出结束；取消属正常关闭路径）。</summary>
    private void StartClock()
    {
        var timer = new PeriodicTimer(ClockInterval);
        _ = Task.Run(async () =>
        {
            try
            {
                while (await timer.WaitForNextTickAsync())
                {
                    var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    // 分钟未变化时不触发通知，避免无谓的 UI 刷新
                    if (text != ClockText)
                    {
                        ClockText = text;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 宿主关闭取消定时器，属正常关闭路径
            }
        });
    }

    /// <summary>导航命令（页面跳转属框架行为，业务逻辑在各页面 VM 内；子项导航自动展开所属分组）。</summary>
    [RelayCommand]
    private void NavigateTo(NavItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (SelectedNav is not null)
        {
            SelectedNav.IsSelected = false;
        }

        CurrentViewModel = item.Target;
        SelectedNav = item;
        item.IsSelected = true;

        // 子项导航：自动展开所属分组，保证菜单层级可见
        foreach (var group in NavItems)
        {
            if (group.Children is not null && group.Children.Contains(item))
            {
                group.IsExpanded = true;
            }
        }

        // 页面生命周期：进入页面时加载该页数据（实现者按需）
        (item.Target as IPageLifecycle)?.OnNavigatedTo();
    }

    /// <summary>切换缩放档位（XAML CommandParameter 为字符串，命令侧解析；非法值忽略）。</summary>
    [RelayCommand]
    private void SetScale(string? scaleText)
    {
        if (double.TryParse(scaleText, out var scale) && ScaleOptions.Contains(scale))
        {
            UiScale = scale;
        }
    }
}
