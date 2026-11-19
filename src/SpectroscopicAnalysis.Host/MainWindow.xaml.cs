using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
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

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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
        LaserMarkingViewModel => new LaserMarkingPage(),
        SettingsViewModel => new SettingsPage(),
        _ => throw new ArgumentException($"未登记的页面 ViewModel：{viewModel.GetType().Name}"),
    };
}
