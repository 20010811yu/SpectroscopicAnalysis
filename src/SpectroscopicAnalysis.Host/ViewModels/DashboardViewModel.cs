using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.Ui;

namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>生产样品行展示模型（主控台「生产样品（今日）」模块）。</summary>
public sealed class ProductionSampleItem
{
    public required string SampleId { get; init; }
    public required string SampleName { get; init; }
    public string? AlloyType { get; init; }
    public required string StatusText { get; init; }
}

/// <summary>
/// 主控台（生产监控）ViewModel：承载「生产样品（今日）」模块。
/// 数据库走游标分页（keyset）：每次仅查询当前页数据（GetProductionPage），不做全量加载；
/// I/O 在后台线程，结果经 IUiDispatcher 回 UI 线程（开发规范第 4 节）。
/// 其余面板随 D5+ 通信与 M2 引擎接入。
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject, IPageLifecycle
{
    /// <summary>分页大小（生产样品表每页行数）。</summary>
    public const int PageSize = 10;

    private readonly IProductionSampleRepository _production;
    private readonly IUiDispatcher _ui;
    private readonly IErrorCollector _errors;

    /// <summary>页游标栈：_pageCursors[i] = 进入第 i+1 页所用的 after 游标（第 1 页无游标）。</summary>
    private readonly List<string> _pageCursors = new();

    /// <summary>当前页行（仅当前页数据，非全量）。</summary>
    private List<ProductionSampleItem> _pageRows = new();

    /// <summary>筛选日期（默认今天；变化后回到第 1 页重查）。</summary>
    [ObservableProperty]
    private DateTime? _selectedDate = DateTime.Today;

    /// <summary>当前页码（从 1 起）。变化不直接查询——由翻页命令驱动（游标方向不同）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageDisplay))]
    [NotifyPropertyChangedFor(nameof(HasPrevPage))]
    [NotifyPropertyChangedFor(nameof(HasNextPage))]
    [NotifyCanExecuteChangedFor(nameof(FirstPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private int _currentPage = 1;

    /// <summary>筛选后总页数（至少 1 页）。变化时刷新翻页命令的 CanExecute。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageDisplay))]
    [NotifyCanExecuteChangedFor(nameof(FirstPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private int _totalPages = 1;

    /// <summary>筛选后总条数（分页栏"共 N 条"）。</summary>
    [ObservableProperty]
    private int _totalCount;

    /// <summary>分页栏显示文本："第 X / Y 页"。</summary>
    public string PageDisplay => $"第 {CurrentPage} / {TotalPages} 页";

    public bool HasPrevPage => CurrentPage > 1;

    public bool HasNextPage => CurrentPage < TotalPages;

    [ObservableProperty]
    private ObservableCollection<ProductionSampleItem> _productionSamples = new();

    /// <summary>是否存在生产样品记录（控制占位说明与表格的可见性）。</summary>
    [ObservableProperty]
    private bool _hasProduction;

    /// <summary>正在加载标记（防重复加载）。</summary>
    [ObservableProperty]
    private bool _isLoading;

    public DashboardViewModel(IProductionSampleRepository production, IUiDispatcher ui, IErrorCollector errors)
    {
        _production = production;
        _ui = ui;
        _errors = errors;
    }

    /// <summary>导航进入时加载（导航路径为 fire-and-forget；测试可 await LoadCommand.ExecuteAsync 保证确定性）。</summary>
    public void OnNavigatedTo() => LoadCommand.Execute(null);

    /// <summary>日期变化：回第 1 页重查。</summary>
    partial void OnSelectedDateChanged(DateTime? value) => _ = ReloadFirstPageAsync();

    /// <summary>加载当前页（I/O 在后台线程：先取总数定页数，再按游标取当页行）。</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var date = SelectedDate;
            var totalCount = await Task.Run(() => _production.Count(date));
            var rows = await Task.Run(() => _production.GetPage(
                date,
                afterSampleId: _pageCursors.Count > 0 ? _pageCursors[^1] : null,
                beforeSampleId: null,
                pageSize: PageSize));

            // 越界收敛（总数变少时当前页可能超出末页）：回退到末页游标重查一次
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));
            if (CurrentPage > totalPages)
            {
                _pageCursors.Clear();
                CurrentPage = totalPages;
                rows = await Task.Run(() => _production.GetPage(date, null, null, PageSize));
            }

            var items = rows.Select(MapItem).ToList();
            _ui.Invoke(() =>
            {
                _pageRows = items;
                ProductionSamples = new ObservableCollection<ProductionSampleItem>(items);
                TotalCount = totalCount;
                TotalPages = totalPages;
                HasProduction = totalCount > 0;
            });
        }
        catch (Exception ex)
        {
            // 加载失败经统一错误链上报（未捕获异常兜底码），不静默吞掉
            _errors.Collect(ErrorCodes.UnhandledException, "生产样品加载失败", null, ex);
        }
        finally
        {
            _ui.Invoke(() => IsLoading = false);
        }
    }

    /// <summary>回首页（已在第 1 页时禁用）：清空游标栈重查。</summary>
    [RelayCommand(CanExecute = nameof(HasPrevPage))]
    private async Task FirstPage()
    {
        _pageCursors.Clear();
        await GoToPageAsync(1);
    }

    /// <summary>上一页（第 1 页时禁用）：以当前页首行 SampleID 为 before 游标反向查询。</summary>
    [RelayCommand(CanExecute = nameof(HasPrevPage))]
    private async Task PrevPage()
    {
        if (_pageRows.Count == 0)
        {
            return;
        }

        var beforeId = _pageRows[0].SampleId;
        if (_pageCursors.Count > 0)
        {
            _pageCursors.RemoveAt(_pageCursors.Count - 1);
        }

        await QueryAndShowAsync(beforeId, isForward: false);
    }

    /// <summary>下一页（末页时禁用）：以当前页末行 SampleID 为 after 游标查询。</summary>
    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private async Task NextPage()
    {
        if (_pageRows.Count == 0)
        {
            return;
        }

        var afterId = _pageRows[^1].SampleId;
        await QueryAndShowAsync(afterId, isForward: true);
    }

    private async Task ReloadFirstPageAsync()
    {
        _pageCursors.Clear();
        CurrentPage = 1;
        await LoadCommand.ExecuteAsync(null);
    }

    /// <summary>按游标方向查询并在 UI 侧更新页码/游标栈。</summary>
    private async Task QueryAndShowAsync(string cursor, bool isForward)
    {
        var targetPage = isForward ? CurrentPage + 1 : CurrentPage - 1;
        var date = SelectedDate;
        try
        {
            var rows = await Task.Run(() => _production.GetPage(
                date,
                afterSampleId: isForward ? cursor : null,
                beforeSampleId: isForward ? null : cursor,
                pageSize: PageSize));
            var items = rows.Select(MapItem).ToList();
            _ui.Invoke(() =>
            {
                _pageRows = items;
                ProductionSamples = new ObservableCollection<ProductionSampleItem>(items);
                if (isForward)
                {
                    _pageCursors.Add(cursor);
                }

                CurrentPage = targetPage;
            });
        }
        catch (Exception ex)
        {
            _errors.Collect(ErrorCodes.UnhandledException, "生产样品翻页失败", null, ex);
        }
    }

    private async Task GoToPageAsync(int page)
    {
        CurrentPage = Math.Clamp(page, 1, TotalPages);
        await LoadCommand.ExecuteAsync(null);
    }

    private static ProductionSampleItem MapItem(ProductionSample r) => new()
    {
        SampleId = r.SampleId,
        SampleName = r.SampleName,
        AlloyType = r.AlloyType,
        StatusText = r.Status switch
        {
            ProductionStatus.Normal => "● 正常",
            ProductionStatus.Analyzing => "● 分析中",
            ProductionStatus.Completed => "✓ 已完成",
            ProductionStatus.Failed => "✖ 失败",
            _ => r.Status,
        },
    };
}
