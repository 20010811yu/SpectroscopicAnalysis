using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.Ui;

namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>当前有效系数行展示模型（CorrectionCoefficient 映射；状态徽章由 IsCurrent 驱动）。</summary>
public sealed class CoefficientGridRow
{
    public required string ElementText { get; init; }
    public required string MethodIdText { get; init; }
    /// <summary>斜率 a（Consolas 展示）。</summary>
    public required string SlopeText { get; init; }
    /// <summary>截距 b。</summary>
    public required string InterceptText { get; init; }
    public required string VersionText { get; init; }
    public required string EffectiveTimeText { get; init; }
    /// <summary>来源设置样/类型标准化样（无来源显示 —）。</summary>
    public required string SourceSampleIdText { get; init; }
    /// <summary>是否当前有效版本（true = "● 当前"，false = "已归档"）。</summary>
    public bool IsCurrent { get; init; }
}

/// <summary>系数变更历史行展示模型（SCT 操作日志中带系数前后值的审计记录）。</summary>
public sealed class CoeffChangeRowItem
{
    public required string TimeText { get; init; }
    public required string SampleIdText { get; init; }
    public required string TriggerReasonText { get; init; }
    /// <summary>变更前系数（a/b 原始串）。</summary>
    public required string CoeffBeforeText { get; init; }
    /// <summary>变更后系数（a/b 原始串）。</summary>
    public required string CoeffAfterText { get; init; }
    public required string ResultStatusText { get; init; }
}

/// <summary>
/// 系数管理 ViewModel：当前有效系数（方法/元素/仅当前筛选 + 内存切片分页）、
/// 变更历史（操作日志中系数前后值审计记录）、干扰矩阵占位。
/// 数据源 CorrectionCoefficient（只读，版本归档不删除）+ SCT_OperationLog；
/// I/O 在后台线程，结果经 IUiDispatcher 回 UI 线程（开发规范第 4 节）。
/// </summary>
public sealed partial class CoefficientsViewModel : ObservableObject, IPageLifecycle
{
    /// <summary>每页行数（两表统一 10 行）。</summary>
    public const int PageSize = 10;

    private readonly ICorrectionCoefficientRepository _coeffs;
    private readonly ISctOperationLogRepository _logs;
    private readonly IUiDispatcher _ui;
    private readonly IErrorCollector _errors;

    /// <summary>分析方法清单（与种子数据方法清单一致；筛选下拉首项加"全部"）。</summary>
    private static readonly string[] MethodOptionsStatic =
    {
        "GB/T 7999-A", "GB/T 7999-B", "ASTM E1251-A", "ASTM E1251-B",
    };

    /// <summary>元素清单（与种子数据元素清单一致）。</summary>
    private static readonly string[] ElementOptionsStatic =
    {
        "Si", "Fe", "Cu", "Mg", "Mn", "Zn", "Cr", "Ni", "Ti", "V",
    };

    /// <summary>方法筛选下拉（首项"全部"）。</summary>
    public IReadOnlyList<string> MethodFilterOptions { get; } =
        new[] { "全部" }.Concat(MethodOptionsStatic).ToList();

    /// <summary>元素筛选下拉（首项"全部"）。</summary>
    public IReadOnlyList<string> ElementFilterOptions { get; } =
        new[] { "全部" }.Concat(ElementOptionsStatic).ToList();

    public CoefficientsViewModel(ICorrectionCoefficientRepository coeffs, ISctOperationLogRepository logs,
        IUiDispatcher ui, IErrorCollector errors)
    {
        _coeffs = coeffs;
        _logs = logs;
        _ui = ui;
        _errors = errors;
    }

    // ==================== Tab 切换 ====================

    /// <summary>激活 Tab（Current=当前有效系数 / History=变更历史 / Matrix=干扰矩阵占位）。</summary>
    [ObservableProperty]
    private string _activeTab = "Current";

    /// <summary>Tab 切换命令（参数为 Tab 键）。</summary>
    [RelayCommand]
    private void SetTab(string tab) => ActiveTab = tab;

    // ==================== 当前有效系数 ====================

    /// <summary>全量系数缓存（GetAll 已按生效时间倒序；筛选/分页仅内存切片）。</summary>
    private List<CorrectionCoefficient> _allCoefficients = new();

    /// <summary>分析方法筛选（"全部" = 不过滤）。</summary>
    [ObservableProperty]
    private string _filterMethod = "全部";

    /// <summary>元素筛选（"全部" = 不过滤）。</summary>
    [ObservableProperty]
    private string _filterElement = "全部";

    /// <summary>仅显示当前有效版本。</summary>
    [ObservableProperty]
    private bool _filterCurrentOnly;

    [ObservableProperty]
    private ObservableCollection<CoefficientGridRow> _coefficientRows = new();

    /// <summary>筛选后总条数（分页页数与空态判断）。</summary>
    [ObservableProperty]
    private int _coeffCount;

    /// <summary>是否存在系数记录（空态切换）。</summary>
    public bool HasCoefficients => CoeffCount > 0;

    /// <summary>是否多页（分页脚显隐）。</summary>
    public bool HasCoeffMultiPage => CoeffTotalPages > 1;

    /// <summary>当前页码（从 1 起）。变化由翻页命令驱动。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoeffPageDisplay))]
    [NotifyPropertyChangedFor(nameof(HasCoeffPrevPage))]
    [NotifyPropertyChangedFor(nameof(HasCoeffNextPage))]
    [NotifyCanExecuteChangedFor(nameof(FirstCoeffPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrevCoeffPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCoeffPageCommand))]
    private int _coeffPage = 1;

    /// <summary>筛选后总页数（至少 1 页）。变化时刷新翻页命令的 CanExecute。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoeffPageDisplay))]
    [NotifyPropertyChangedFor(nameof(HasCoeffMultiPage))]
    [NotifyCanExecuteChangedFor(nameof(NextCoeffPageCommand))]
    private int _coeffTotalPages = 1;

    /// <summary>分页栏显示文本："第 X / Y 页"。</summary>
    public string CoeffPageDisplay => $"第 {CoeffPage} / {CoeffTotalPages} 页";

    public bool HasCoeffPrevPage => CoeffPage > 1;

    public bool HasCoeffNextPage => CoeffPage < CoeffTotalPages;

    /// <summary>分页脚页码跳转输入框。</summary>
    [ObservableProperty]
    private string _coeffPageJumpInput = string.Empty;

    /// <summary>筛选变化：回第 1 页重切。</summary>
    partial void OnFilterMethodChanged(string value) => ShowCoeffPage(1);

    partial void OnFilterElementChanged(string value) => ShowCoeffPage(1);

    partial void OnFilterCurrentOnlyChanged(bool value) => ShowCoeffPage(1);

    /// <summary>筛选后的系数序列（方法/元素/仅当前三级过滤）。</summary>
    private IEnumerable<CorrectionCoefficient> FilteredCoefficients()
    {
        var rows = _allCoefficients.AsEnumerable();
        if (FilterMethod != "全部")
        {
            rows = rows.Where(c => c.MethodId == FilterMethod);
        }

        if (FilterElement != "全部")
        {
            rows = rows.Where(c => c.Element == FilterElement);
        }

        if (FilterCurrentOnly)
        {
            rows = rows.Where(c => c.IsCurrent);
        }

        return rows;
    }

    /// <summary>切片显示指定页（越界收敛到末页）。</summary>
    private void ShowCoeffPage(int page)
    {
        var rows = FilteredCoefficients().ToList();
        var totalPages = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)PageSize));
        page = Math.Clamp(page, 1, totalPages);

        _ui.Invoke(() =>
        {
            CoefficientRows = new ObservableCollection<CoefficientGridRow>(rows
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .Select(MapCoefficient));
            CoeffCount = rows.Count;
            CoeffTotalPages = totalPages;
            CoeffPage = page;
            OnPropertyChanged(nameof(HasCoefficients));
        });
    }

    /// <summary>系数行映射（数值 6 位有效；无来源显示 —）。</summary>
    private static CoefficientGridRow MapCoefficient(CorrectionCoefficient c) => new()
    {
        ElementText = c.Element,
        MethodIdText = c.MethodId,
        SlopeText = c.Slope.ToString("0.######"),
        InterceptText = c.Intercept.ToString("0.######"),
        VersionText = $"v{c.Version}",
        EffectiveTimeText = c.EffectiveTime,
        SourceSampleIdText = string.IsNullOrWhiteSpace(c.SourceSampleId) ? "—" : c.SourceSampleId,
        IsCurrent = c.IsCurrent,
    };

    /// <summary>首页（已在第 1 页时禁用）。</summary>
    [RelayCommand(CanExecute = nameof(HasCoeffPrevPage))]
    private void FirstCoeffPage() => ShowCoeffPage(1);

    /// <summary>上一页。</summary>
    [RelayCommand(CanExecute = nameof(HasCoeffPrevPage))]
    private void PrevCoeffPage() => ShowCoeffPage(CoeffPage - 1);

    /// <summary>下一页。</summary>
    [RelayCommand(CanExecute = nameof(HasCoeffNextPage))]
    private void NextCoeffPage() => ShowCoeffPage(CoeffPage + 1);

    /// <summary>页脚「跳转」（解析失败忽略不清空，便于修正）。</summary>
    [RelayCommand]
    private void JumpToCoeffPage()
    {
        if (!int.TryParse(CoeffPageJumpInput?.Trim(), out var page))
        {
            return;
        }

        CoeffPageJumpInput = string.Empty;
        ShowCoeffPage(page);
    }

    // ==================== 变更历史 ====================

    [ObservableProperty]
    private ObservableCollection<CoeffChangeRowItem> _changeRows = new();

    /// <summary>变更历史总条数（分页与空态判断）。</summary>
    [ObservableProperty]
    private int _changeCount;

    /// <summary>是否存在变更记录（空态切换）。</summary>
    public bool HasChanges => ChangeCount > 0;

    /// <summary>变更历史是否多页。</summary>
    public bool HasChangeMultiPage => ChangeTotalPages > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangePageDisplay))]
    [NotifyPropertyChangedFor(nameof(HasChangePrevPage))]
    [NotifyPropertyChangedFor(nameof(HasChangeNextPage))]
    [NotifyCanExecuteChangedFor(nameof(FirstChangePageCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrevChangePageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextChangePageCommand))]
    private int _changePage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangePageDisplay))]
    [NotifyPropertyChangedFor(nameof(HasChangeMultiPage))]
    [NotifyCanExecuteChangedFor(nameof(NextChangePageCommand))]
    private int _changeTotalPages = 1;

    /// <summary>变更历史分页栏显示文本。</summary>
    public string ChangePageDisplay => $"第 {ChangePage} / {ChangeTotalPages} 页";

    public bool HasChangePrevPage => ChangePage > 1;

    public bool HasChangeNextPage => ChangePage < ChangeTotalPages;

    /// <summary>变更历史页码跳转输入框。</summary>
    [ObservableProperty]
    private string _changePageJumpInput = string.Empty;

    /// <summary>变更历史全量缓存（加载时已映射为行模型，翻页仅内存切片）。</summary>
    private List<CoeffChangeRowItem> _allChanges = new();

    /// <summary>切片显示变更历史指定页（越界收敛到末页）。</summary>
    private void ShowChangePage(int page)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(_allChanges.Count / (double)PageSize));
        page = Math.Clamp(page, 1, totalPages);

        _ui.Invoke(() =>
        {
            ChangeRows = new ObservableCollection<CoeffChangeRowItem>(
                _allChanges.Skip((page - 1) * PageSize).Take(PageSize));
            ChangeCount = _allChanges.Count;
            ChangeTotalPages = totalPages;
            ChangePage = page;
            OnPropertyChanged(nameof(HasChanges));
        });
    }

    /// <summary>首页（已在第 1 页时禁用）。</summary>
    [RelayCommand(CanExecute = nameof(HasChangePrevPage))]
    private void FirstChangePage() => ShowChangePage(1);

    /// <summary>上一页。</summary>
    [RelayCommand(CanExecute = nameof(HasChangePrevPage))]
    private void PrevChangePage() => ShowChangePage(ChangePage - 1);

    /// <summary>下一页。</summary>
    [RelayCommand(CanExecute = nameof(HasChangeNextPage))]
    private void NextChangePage() => ShowChangePage(ChangePage + 1);

    /// <summary>页脚「跳转」（解析失败忽略不清空，便于修正）。</summary>
    [RelayCommand]
    private void JumpToChangePage()
    {
        if (!int.TryParse(ChangePageJumpInput?.Trim(), out var page))
        {
            return;
        }

        ChangePageJumpInput = string.Empty;
        ShowChangePage(page);
    }

    /// <summary>变更历史行映射（时间解析失败保留原始串）。</summary>
    private static CoeffChangeRowItem MapChange(SctOperationLog l)
    {
        var time = DateTime.TryParse(l.OperationTime, out var t) ? t.ToString("yyyy-MM-dd HH:mm:ss") : l.OperationTime;
        return new CoeffChangeRowItem
        {
            TimeText = time,
            SampleIdText = l.SampleId ?? "—",
            TriggerReasonText = l.TriggerReason ?? "—",
            CoeffBeforeText = l.CoeffBefore ?? "—",
            CoeffAfterText = l.CoeffAfter ?? "—",
            ResultStatusText = l.ResultStatus switch
            {
                AnalysisResultStatus.Success => "● 成功",
                AnalysisResultStatus.Warning => "▲ 预警",
                AnalysisResultStatus.Fail => "✖ 失败",
                _ => l.ResultStatus,
            },
        };
    }

    // ==================== 加载 ====================

    /// <summary>导航进入时加载（I/O 后台线程；加载失败经统一错误链上报，不静默吞掉）。</summary>
    public void OnNavigatedTo()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var coeffs = await Task.Run(() => _coeffs.GetAll().ToList());
                var changes = await Task.Run(() => _logs.GetAll()
                    .Where(l => l.CoeffBefore is not null || l.CoeffAfter is not null)
                    .Select(MapChange)
                    .ToList());

                _ui.Invoke(() =>
                {
                    _allCoefficients = coeffs;
                    _allChanges = changes;
                });
                ShowCoeffPage(1);
                ShowChangePage(1);
            }
            catch (Exception ex)
            {
                _errors.Collect(ErrorCodes.UnhandledException, "系数管理加载失败", null, ex);
            }
        });
    }
}
