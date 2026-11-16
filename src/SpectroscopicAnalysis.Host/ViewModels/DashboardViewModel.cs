using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.Ui;

namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>
/// 主控台（生产监控）ViewModel：承载「运行日志（按日期）」模块。
/// 数据源为 SCT 操作日志（GetAll 全量 + 内存切片分页，SQL 已按操作时间倒序）；
/// I/O 在后台线程，结果经 IUiDispatcher 回 UI 线程（开发规范第 4 节）。
/// 其余面板随 D5+ 通信与 M2 引擎接入。
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject, IPageLifecycle
{
    /// <summary>分页大小（运行日志表每页行数）。</summary>
    public const int PageSize = 10;

    private readonly ISctOperationLogRepository _logs;
    private readonly ISctSampleRepository _samples;
    private readonly IProductionSampleRepository _production;
    private readonly IUiDispatcher _ui;
    private readonly IErrorCollector _errors;

    /// <summary>SampleID → 样品类型键映射（类型筛选用；SCT 四类取自 SCT_Sample，生产样取自 Production_Sample）。</summary>
    private Dictionary<string, string> _sampleTypeMap = new();

    /// <summary>全量日志（已按操作时间倒序；翻页仅做内存切片）。</summary>
    private List<OperationLogRowItem> _allLogs = new();

    /// <summary>筛选日期（默认今天；变化后回到第 1 页重查）。</summary>
    [ObservableProperty]
    private DateTime? _selectedDate = DateTime.Today;

    /// <summary>当前页码（从 1 起）。变化由翻页命令驱动。</summary>
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
    private ObservableCollection<OperationLogRowItem> _logRows = new();

    /// <summary>是否存在日志记录（控制占位说明与表格的可见性）。</summary>
    [ObservableProperty]
    private bool _hasLogs;

    /// <summary>正在加载标记（防重复加载）。</summary>
    [ObservableProperty]
    private bool _isLoading;

    public DashboardViewModel(ISctOperationLogRepository logs, ISctSampleRepository samples,
        IProductionSampleRepository production, IUiDispatcher ui, IErrorCollector errors)
    {
        _logs = logs;
        _samples = samples;
        _production = production;
        _ui = ui;
        _errors = errors;
    }

    /// <summary>类型筛选键（All/SettingUp/Control/TypeStd/Production；变化后回第 1 页重切）。</summary>
    [ObservableProperty]
    private string _filterKey = "All";

    /// <summary>导航进入时加载（导航路径为 fire-and-forget；测试可 await LoadCommand.ExecuteAsync 保证确定性）。</summary>
    public void OnNavigatedTo() => LoadCommand.Execute(null);

    /// <summary>日期变化：回第 1 页重查。</summary>
    partial void OnSelectedDateChanged(DateTime? value) => _ = ReloadFirstPageAsync();

    /// <summary>类型变化：回第 1 页重切（内存过滤，不重查库）。</summary>
    partial void OnFilterKeyChanged(string value) => _ = ReloadFirstPageAsync();

    /// <summary>类型筛选按钮命令（参数为类型键）。</summary>
    [RelayCommand]
    private void SetFilter(string key)
    {
        FilterKey = key;
    }

    /// <summary>加载全量日志并按当前页切片（I/O 在后台线程）。</summary>
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
            _allLogs = await Task.Run(() => _logs.GetAll()
                .Select(MapLogRow)
                .ToList());
            _sampleTypeMap = await Task.Run(() =>
            {
                var map = new Dictionary<string, string>();
                foreach (var s in _samples.GetAll())
                {
                    map[s.SampleId] = s.SampleType;
                }
                foreach (var p in _production.GetAll())
                {
                    map[p.SampleId] = SampleType.Production;
                }
                return map;
            });

            _ui.Invoke(() =>
            {
                TotalCount = FilteredLogs().Count;
                TotalPages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
                // 越界收敛（总数变少时当前页可能超出末页）
                if (CurrentPage > TotalPages)
                {
                    CurrentPage = TotalPages;
                }

                ShowPage(CurrentPage);
                HasLogs = TotalCount > 0;
            });
        }
        catch (Exception ex)
        {
            // 加载失败经统一错误链上报（未捕获异常兜底码），不静默吞掉
            _errors.Collect(ErrorCodes.UnhandledException, "运行日志加载失败", null, ex);
        }
        finally
        {
            _ui.Invoke(() => IsLoading = false);
        }
    }

    /// <summary>按筛选日期过滤日志（OperationTime 字符串解析比较日期）。</summary>
    private List<OperationLogRowItem> FilteredLogs()
    {
        var date = SelectedDate?.Date;
        if (date is null)
        {
            return _allLogs;
        }

        var rows = _allLogs
            .Where(l => DateTime.TryParse(l.RawOperationTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) && t.Date == date.Value);
        if (FilterKey != "All")
        {
            rows = rows.Where(l => _sampleTypeMap.TryGetValue(l.RawSampleId, out var type) && type == FilterKey);
        }
        return rows.ToList();
    }

    /// <summary>内存切片显示指定页（FilteredLogs 已时间倒序）。</summary>
    private void ShowPage(int page)
    {
        var rows = FilteredLogs()
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .ToList();
        LogRows = new ObservableCollection<OperationLogRowItem>(rows);
    }

    /// <summary>回首页（已在第 1 页时禁用）。</summary>
    [RelayCommand(CanExecute = nameof(HasPrevPage))]
    private async Task FirstPage()
    {
        await GoToPageAsync(1);
    }

    /// <summary>上一页（第 1 页时禁用）。</summary>
    [RelayCommand(CanExecute = nameof(HasPrevPage))]
    private async Task PrevPage()
    {
        await GoToPageAsync(CurrentPage - 1);
    }

    /// <summary>下一页（末页时禁用）。</summary>
    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private async Task NextPage()
    {
        await GoToPageAsync(CurrentPage + 1);
    }

    private async Task ReloadFirstPageAsync()
    {
        CurrentPage = 1;
        await LoadCommand.ExecuteAsync(null);
    }

    private async Task GoToPageAsync(int page)
    {
        CurrentPage = Math.Clamp(page, 1, TotalPages);
        ShowPage(CurrentPage);
        await Task.CompletedTask;
    }

    /// <summary>分页脚页码输入框（跳页用；解析失败忽略不清空，便于修正）。</summary>
    [ObservableProperty]
    private string _pageJumpInput = string.Empty;

    /// <summary>页脚「跳转」：内存切片分页可直接定位任意页。</summary>
    [RelayCommand]
    private async Task JumpToPageAsync()
    {
        if (!int.TryParse(PageJumpInput?.Trim(), out var page))
        {
            return;
        }

        PageJumpInput = string.Empty;
        await GoToPageAsync(Math.Clamp(page, 1, TotalPages));
    }

    /// <summary>日志行映射（操作类型/结果状态中文映射；时间保留原始串供日期过滤与末列展示）。</summary>
    private static OperationLogRowItem MapLogRow(SctOperationLog l)
    {
        var time = DateTime.TryParse(l.OperationTime, out var t) ? t : DateTime.MinValue;
        return new OperationLogRowItem
        {
            LogId = l.LogId,
            SampleIdText = l.SampleId ?? "—",
            RawSampleId = l.SampleId ?? string.Empty,
            OperationTypeText = l.OperationType switch
            {
                "Standardization" => "标准化",
                "ControlCheck" => "控制检查",
                "HeightMeasure" => "测高",
                _ => l.OperationType,
            },
            TriggerReasonText = l.TriggerReason ?? "—",
            OperationTimeText = time.ToString("MM-dd HH:mm"),
            ResultStatusText = l.ResultStatus switch
            {
                AnalysisResultStatus.Success => "● 成功",
                AnalysisResultStatus.Warning => "▲ 预警",
                AnalysisResultStatus.Fail => "✖ 失败",
                _ => l.ResultStatus,
            },
            RawOperationTime = l.OperationTime,
        };
    }
}
