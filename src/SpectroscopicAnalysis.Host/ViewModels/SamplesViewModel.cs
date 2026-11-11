using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.Ui;

namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>
/// 台账行展示模型：领域数据 → 展示文本映射（开发规范 3.3：VM 负责映射为展示模型）。
/// SampleTypeKey 为类型键（筛选用，非绑定字段）；Status 为原始枚举串（视图 DataTrigger 切换徽章颜色）。
/// </summary>
public sealed partial class SampleRowItem : ObservableObject
{
    public required string SampleId { get; init; }
    public required string SampleName { get; init; }
    public required string SampleTypeKey { get; init; }
    public required string TypeDisplay { get; init; }
    public string? AlloyType { get; init; }
    public string? RackPosition { get; init; }
    public required string AssociatedMethodText { get; init; }
    public required string ThicknessText { get; init; }
    public required string MinThicknessText { get; init; }
    public required string UsageText { get; init; }
    public required string Status { get; init; }
    public required string StatusText { get; init; }
    public required string LastHeightText { get; init; }
    public required string LastUseText { get; init; }
}

/// <summary>
/// 样品档案 ViewModel：从 SQLite（SCT_Sample）加载样品并按类型筛选展示。
/// 数据库 I/O 经 Task.Run + IUiDispatcher 回 UI 线程（I/O 不阻塞 UI，开发规范第 4 节）。
/// </summary>
public sealed partial class SamplesViewModel : ObservableObject, IPageLifecycle
{
    public const string FilterAll = "All";

    private readonly ISctSampleRepository _samples;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IErrorCollector _errors;

    /// <summary>全部台账行（非生产样品），未筛选。</summary>
    private List<SampleRowItem> _allSctRows = new();

    [ObservableProperty]
    private ObservableCollection<SampleRowItem> _sctSamples = new();

    [ObservableProperty]
    private ObservableCollection<SampleRowItem> _productionSamples = new();

    /// <summary>当前筛选键（All/SettingUp/Control/TypeStd/Production）。</summary>
    [ObservableProperty]
    private string _filterKey = FilterAll;

    /// <summary>状态筛选项（Key=null 表示全部；Key 为 SampleStatus 常量）。</summary>
    public sealed record StatusOption(string? Key, string DisplayName)
    {
        /// <summary>「全部状态」选项（下拉默认选中项）。</summary>
        public static StatusOption All { get; } = new(null, "全部状态");
    }

    /// <summary>状态下拉可选项（与任务清单状态机枚举一致，不自创状态名）。</summary>
    public IReadOnlyList<StatusOption> StatusOptions { get; } = new StatusOption[]
    {
        StatusOption.All,
        new(SampleStatus.Normal, "正常"),
        new(SampleStatus.NeedMeasure, "需测高"),
        new(SampleStatus.Warning, "超差预警"),
        new(SampleStatus.Disabled, "禁用（过薄）"),
    };

    /// <summary>当前状态筛选（默认「全部状态」；变化后回到第 1 页刷新）。</summary>
    [ObservableProperty]
    private StatusOption _statusFilter = StatusOption.All;

    partial void OnStatusFilterChanged(StatusOption value) => RefreshView(resetPage: true);

    /// <summary>查询关键词（模糊匹配样品编号/名称；空即不过滤）。</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>分页大小（台账每页行数）。</summary>
    public const int PageSize = 15;

    /// <summary>当前页码（从 1 起）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageDisplay))]
    [NotifyPropertyChangedFor(nameof(HasPrevPage))]
    [NotifyPropertyChangedFor(nameof(HasNextPage))]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private int _currentPage = 1;

    /// <summary>筛选后总页数（至少 1 页）。变化时刷新翻页命令的 CanExecute。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageDisplay))]
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

    /// <summary>生产样品是否存在记录（控制空态文案显隐）。</summary>
    [ObservableProperty]
    private bool _hasProduction;

    /// <summary>正在加载标记（防重复加载）。</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>删除失败提示（空即无错误；如数据库约束冲突）。</summary>
    [ObservableProperty]
    private string? _deleteError;

    // ==================== 样品注册（任务清单"入库初始化"） ====================

    /// <summary>样品类型下拉选项（类型键：设置样/控制样/类型标准化样；生产样品随分析流程入库，不开放录入）。
    /// 必须为实例属性供 XAML ItemsSource 绑定（静态字段不可绑定）。</summary>
    public IReadOnlyList<string> SampleTypeOptions { get; } = new[]
    {
        SampleType.SettingUp, SampleType.Control, SampleType.TypeStd,
    };

    [ObservableProperty]
    private bool _isRegisterOpen;

    [ObservableProperty]
    private string _regSampleId = string.Empty;

    [ObservableProperty]
    private string _regSampleName = string.Empty;

    [ObservableProperty]
    private string _regSampleType = SampleType.SettingUp;

    [ObservableProperty]
    private string? _regAlloyType;

    [ObservableProperty]
    private string? _regRackPosition;

    [ObservableProperty]
    private string _regInitialThickness = string.Empty;

    [ObservableProperty]
    private string _regMinThickness = string.Empty;

    [ObservableProperty]
    private string _regMaxUsageCount = string.Empty;

    /// <summary>注册校验错误提示（空即无错误）。</summary>
    [ObservableProperty]
    private string? _registerError;

    /// <summary>注册提交中标记（防重复提交）。</summary>
    [ObservableProperty]
    private bool _isRegistering;

    /// <summary>切换注册表单显隐。</summary>
    [RelayCommand]
    private void ToggleRegister() => IsRegisterOpen = !IsRegisterOpen;

    /// <summary>取消注册：清空表单与错误并关闭面板。</summary>
    [RelayCommand]
    private void CancelRegister()
    {
        ClearRegisterForm();
        IsRegisterOpen = false;
    }

    /// <summary>提交注册：校验 → 入库初始化（CurrentThickness=Initial、UsageCount=0、Status=Normal）→ 刷新。</summary>
    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (IsRegistering)
        {
            return;
        }

        var error = ValidateRegisterForm();
        if (error is not null)
        {
            RegisterError = error;
            return;
        }

        var sample = BuildSampleFromForm();
        IsRegistering = true;
        RegisterError = null;
        try
        {
            // 重复校验聚合（编号/架位一次后台查询完成，全部通过才入库）
            var dupError = await Task.Run(() =>
            {
                var errors = new List<string>();
                if (_samples.GetById(sample.SampleId) is not null)
                {
                    errors.Add($"样品编号已存在：{sample.SampleId}");
                }
                var occupant = _samples.GetAll().FirstOrDefault(s =>
                    string.Equals(s.RackPosition?.Trim(), sample.RackPosition, StringComparison.Ordinal));
                if (occupant is not null)
                {
                    errors.Add($"货架位置已占用：{sample.RackPosition}（现有样品 {occupant.SampleId}）");
                }
                return errors.Count > 0 ? string.Join("\n", errors) : null;
            });
            if (dupError is not null)
            {
                RegisterError = dupError;
                _errors.Collect(ErrorCodes.SampleRegisterRejected, dupError, sample.SampleId);
                return;
            }

            try
            {
                await Task.Run(() => _samples.Insert(sample));
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                RegisterError = $"入库失败：{ex.Message}";
                _errors.Collect(ErrorCodes.SampleInsertFailed, "样品入库写库失败", sample.SampleId, ex);
                return;
            }
            _ui.Invoke(() =>
            {
                ClearRegisterForm();
                IsRegisterOpen = false;
            });
            // 入库后刷新列表（LoadAsync 内部有 IsLoading 防抖，此处直接复用）
            LoadCommand.Execute(null);
        }
        finally
        {
            _ui.Invoke(() => IsRegistering = false);
        }
    }

    /// <summary>表单校验；返回错误消息，null 表示通过。</summary>
    private string? ValidateRegisterForm()
    {
        if (string.IsNullOrWhiteSpace(RegSampleId))
        {
            return "样品编号不能为空";
        }
        if (string.IsNullOrWhiteSpace(RegSampleName))
        {
            return "样品名称不能为空";
        }
        if (string.IsNullOrWhiteSpace(RegRackPosition))
        {
            return "样品架位置不能为空";
        }
        if (!double.TryParse(RegInitialThickness, out var init) || init <= 0)
        {
            return "初始厚度必须为大于 0 的数值";
        }
        if (!double.TryParse(RegMinThickness, out var min) || min <= 0)
        {
            return "最小厚度必须为大于 0 的数值";
        }
        if (init <= min)
        {
            return "初始厚度必须大于最小厚度";
        }
        if (!int.TryParse(RegMaxUsageCount, out var maxUsage) || maxUsage < 1)
        {
            return "最大使用次数必须为不小于 1 的整数";
        }
        return null;
    }

    private SctSample BuildSampleFromForm() => new()
    {
        SampleId = RegSampleId.Trim(),
        SampleName = RegSampleName.Trim(),
        SampleType = TypeDisplayToKey(RegSampleType),
        AlloyType = string.IsNullOrWhiteSpace(RegAlloyType) ? null : RegAlloyType.Trim(),
        RackPosition = RegRackPosition?.Trim(), // 校验已确保非空
        InitialThickness = double.Parse(RegInitialThickness),
        CurrentThickness = double.Parse(RegInitialThickness), // 入库初始化：当前厚度 = 初始厚度
        MinThickness = double.Parse(RegMinThickness),
        UsageCount = 0,
        MaxUsageCount = int.Parse(RegMaxUsageCount),
        Status = SampleStatus.Normal,
        IsActive = true,
        CreatedTime = DateTime.Now,
    };

    private void ClearRegisterForm()
    {
        RegSampleId = string.Empty;
        RegSampleName = string.Empty;
        RegSampleType = SampleTypeOptions[0];
        RegAlloyType = null;
        RegRackPosition = null;
        RegInitialThickness = string.Empty;
        RegMinThickness = string.Empty;
        RegMaxUsageCount = string.Empty;
        RegisterError = null;
    }

    /// <summary>下拉选项 → 类型键（选项本身即类型键；保留归一化入口便于后续扩展展示别名）。</summary>
    private static string TypeDisplayToKey(string display) => display switch
    {
        SampleType.SettingUp or SampleType.Control or SampleType.TypeStd or SampleType.Production => display,
        "设置样" => SampleType.SettingUp,
        "控制样" => SampleType.Control,
        "类型标准化样" => SampleType.TypeStd,
        "生产样品" => SampleType.Production,
        _ => display,
    };

    public SamplesViewModel(ISctSampleRepository samples, IUiDispatcher ui, IDialogService dialogs, IErrorCollector errors)
    {
        _samples = samples;
        _ui = ui;
        _dialogs = dialogs;
        _errors = errors;
    }

    /// <summary>导航进入时加载（导航路径为 fire-and-forget；测试可 await LoadCommand.ExecuteAsync 保证确定性）。</summary>
    public void OnNavigatedTo() => LoadCommand.Execute(null);

    /// <summary>从数据库加载样品并刷新两个表格（I/O 在后台线程，结果经 IUiDispatcher 回 UI）。</summary>
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
            var rows = await Task.Run(() => _samples.GetAll().Select(MapRow).ToList());
            _ui.Invoke(() =>
            {
                _allSctRows = rows.Where(r => r.SampleTypeKey != SampleType.Production).ToList();
                var production = rows.Where(r => r.SampleTypeKey == SampleType.Production).ToList();
                HasProduction = production.Count > 0;
                ApplyFilter(FilterKey);
                ProductionSamples = new ObservableCollection<SampleRowItem>(production);
            });
        }
        finally
        {
            _ui.Invoke(() => IsLoading = false);
        }
    }

    /// <summary>切换类型筛选。</summary>
    [RelayCommand]
    private void SetFilter(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        FilterKey = key;
        // 切换类型筛选回到第 1 页
        RefreshView(resetPage: true);
    }

    private void ApplyFilter(string key)
    {
        FilterKey = key;
        RefreshView();
    }

    /// <summary>搜索命令：关键词变化后回到第 1 页刷新（回车/按钮触发）。</summary>
    [RelayCommand]
    private void Search() => RefreshView(resetPage: true);

    /// <summary>类型筛选 + 关键词过滤 + 分页切片统一入口。</summary>
    private void RefreshView(bool resetPage = false)
    {
        var keyword = SearchText?.Trim();
        var filtered = FilterKey == FilterAll
            ? _allSctRows
            : _allSctRows.Where(r => r.SampleTypeKey == FilterKey);
        // 状态筛选（null = 全部状态）
        if (!string.IsNullOrEmpty(StatusFilter?.Key))
        {
            filtered = filtered.Where(r => r.Status == StatusFilter.Key);
        }
        if (!string.IsNullOrEmpty(keyword))
        {
            filtered = filtered.Where(r =>
                r.SampleId.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                r.SampleName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        var list = filtered.ToList();
        TotalCount = list.Count;
        TotalPages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)PageSize));
        if (resetPage || CurrentPage > TotalPages)
        {
            CurrentPage = resetPage ? 1 : TotalPages;
        }

        var pageRows = list.Skip((CurrentPage - 1) * PageSize).Take(PageSize);
        SctSamples = new ObservableCollection<SampleRowItem>(pageRows);
    }

    /// <summary>上一页（第 1 页时禁用）。</summary>
    [RelayCommand(CanExecute = nameof(HasPrevPage))]
    private void PrevPage() => GoToPage(CurrentPage - 1);

    /// <summary>下一页（末页时禁用）。</summary>
    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private void NextPage() => GoToPage(CurrentPage + 1);

    /// <summary>跳转到指定页码（越界收敛到有效范围）。</summary>
    [RelayCommand]
    private void GoToPage(int page)
    {
        var target = Math.Clamp(page, 1, TotalPages);
        if (target == CurrentPage)
        {
            return;
        }

        CurrentPage = target;
        RefreshView();
    }

    // ==================== 样品删除（弹窗确认，危险操作） ====================

    /// <summary>删除：先弹窗确认（不动行数据），确认后事务级联删除（样品+标准值+控制限；操作日志保留）。</summary>
    [RelayCommand]
    private async Task DeleteAsync(SampleRowItem? row)
    {
        if (row is null || IsLoading)
        {
            return;
        }

        DeleteError = null;
        if (!_dialogs.Confirm("删除样品", $"确认删除样品「{row.SampleId} {row.SampleName}」？\n将级联删除其标准值与控制限（操作日志保留）。"))
        {
            return;
        }

        var snapshot = row;
        try
        {
            await Task.Run(() => _samples.DeleteWithChildren(snapshot.SampleId));
            _ui.Invoke(() =>
            {
                _allSctRows = _allSctRows.Where(r => !ReferenceEquals(r, snapshot)).ToList();
                ProductionSamples = new ObservableCollection<SampleRowItem>(
                    ProductionSamples.Where(r => !ReferenceEquals(r, snapshot)));
                ApplyFilter(FilterKey);
                HasProduction = ProductionSamples.Count > 0;
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            DeleteError = $"删除失败：{ex.Message}";
            _errors.Collect(ErrorCodes.SampleDeleteFailed, "样品删除失败", snapshot.SampleId, ex);
        }
    }

    private static SampleRowItem MapRow(SctSample s) => new()
    {
        SampleId = s.SampleId,
        SampleName = s.SampleName,
        SampleTypeKey = s.SampleType,
        TypeDisplay = s.SampleType switch
        {
            SampleType.SettingUp => "设置样",
            SampleType.Control => "控制样",
            SampleType.TypeStd => "类型标准化样",
            SampleType.Production => "生产样品",
            _ => s.SampleType,
        },
        AlloyType = s.AlloyType,
        RackPosition = s.RackPosition,
        AssociatedMethodText = s.AssociatedMethod ?? string.Empty,
        ThicknessText = $"{s.CurrentThickness:0.0#} / {s.InitialThickness:0.0#}",
        MinThicknessText = $"{s.MinThickness:0.0#}",
        UsageText = $"{s.UsageCount}/{s.MaxUsageCount}",
        Status = s.Status,
        StatusText = s.Status switch
        {
            SampleStatus.Normal => "● 正常",
            SampleStatus.NeedMeasure => "▲ 需测高",
            SampleStatus.Warning => "▲ 超差预警",
            SampleStatus.Disabled => "✖ 禁用（过薄）",
            _ => s.Status,
        },
        LastHeightText = s.LastHeightCheckTime?.ToString("MM-dd HH:mm") ?? "—",
        LastUseText = s.LastUseTime?.ToString("MM-dd HH:mm") ?? "—",
    };
}
