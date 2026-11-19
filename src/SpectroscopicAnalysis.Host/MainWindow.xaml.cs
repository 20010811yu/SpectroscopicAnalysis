using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Controls.Primitives;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.ViewModels;
using SpectroscopicAnalysis.Host.Views;

namespace SpectroscopicAnalysis.Host;

/// <summary>
/// 主窗口（组合根装配 DataContext）。页面承载采用"按 ViewModel 缓存视图实例"策略：
/// 每个页面仅在首次进入时构建一次视觉树，切换导航仅复用实例（避免整页 DataGrid 重建导致的切换卡顿）。
/// 纯视图层行为：仅做 VM→View 映射与实例复用，不含任何业务逻辑（开发规范 3.3）。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>页面缓存（键 = 页面 ViewModel 实例；页面与 VM 一一对应）。</summary>
    private readonly Dictionary<object, UserControl> _pageCache = new();

    private MainViewModel? _navSource;

    /// <summary>全流程日志（软件上任何按钮操作统一在此留痕）。</summary>
    private readonly ISystemLogCollector _systemLog;

    /// <summary>页面 VM 类型 → 中文名（操作留痕标注来源页面）。</summary>
    private static readonly Dictionary<string, string> PageTitles = new()
    {
        [nameof(ViewModels.ConsoleViewModel)] = "主控台",
        [nameof(ViewModels.ProductionMonitorViewModel)] = "生产监控",
        [nameof(ViewModels.SamplesViewModel)] = "样品档案",
        [nameof(ViewModels.SctViewModel)] = "SCT 管理",
        [nameof(ViewModels.CoefficientsViewModel)] = "系数管理",
        [nameof(ViewModels.AlarmsViewModel)] = "日志报警",
        [nameof(ViewModels.SettingsViewModel)] = "设置",
    };

    public MainWindow(ISystemLogCollector systemLog)
    {
        _systemLog = systemLog;
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // 全局按钮操作留痕：冒泡阶段捕获主窗口内所有按钮点击（含已处理事件），统一写全流程日志
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnAnyButtonClick), handledEventsToo: true);
    }

    /// <summary>任何按钮点击 → 全流程日志「UI操作」类目（内容为按钮文本，附来源页面）。</summary>
    private void OnAnyButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { } fe || fe is not ButtonBase button)
        {
            return;
        }

        var text = button.Content switch
        {
            string str => str,
            FrameworkElement { } el => el.Tag as string ?? string.Empty,
            _ => string.Empty,
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var page = (PageHost.Content as FrameworkElement)?.DataContext?.GetType().Name is { } typeName
                   && PageTitles.TryGetValue(typeName, out var title)
            ? title
            : "主窗口";
        _systemLog.Log("UI操作", $"{page}：{text}");
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_navSource is not null)
        {
            _navSource.PropertyChanged -= NavSourcePropertyChanged;
        }

        _navSource = e.NewValue as MainViewModel;
        if (_navSource is not null)
        {
            _navSource.PropertyChanged += NavSourcePropertyChanged;
            ShowPage(_navSource.CurrentViewModel);
        }
    }

    private void NavSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentViewModel) && _navSource is not null)
        {
            ShowPage(_navSource.CurrentViewModel);
        }
    }

    /// <summary>展示目标页（缓存未命中时构建一次并绑定 DataContext）。</summary>
    private void ShowPage(ObservableObject? viewModel)
    {
        if (viewModel is null)
        {
            PageHost.Content = null;
            return;
        }

        if (!_pageCache.TryGetValue(viewModel, out var page))
        {
            page = CreatePage(viewModel);
            page.DataContext = viewModel;
            _pageCache[viewModel] = page;
        }

        PageHost.Content = page;
    }

    /// <summary>VM→View 映射（与 App.xaml 的 DataTemplate 一一对应；新增页面两处同步登记）。</summary>
    private static UserControl CreatePage(ObservableObject viewModel) => viewModel switch
    {
        ProductionMonitorViewModel => new ProductionMonitorPage(),
        ConsoleViewModel => new ConsolePage(),
        SamplesViewModel => new SamplesPage(),
        SctViewModel => new SctPage(),
        CoefficientsViewModel => new CoefficientsPage(),
        AlarmsViewModel => new AlarmsPage(),
        SettingsViewModel => new SettingsPage(),
        _ => throw new ArgumentException($"未登记的页面 ViewModel：{viewModel.GetType().Name}"),
    };
}
