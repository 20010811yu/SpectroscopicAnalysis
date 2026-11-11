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

    // ==================== 数据表切换键（样品档案页可查看的 5 张表） ====================
    public const string TableSample = "Sample";
    public const string TableCertifiedValue = "CertifiedValue";
    public const string TableControlLimit = "ControlLimit";
    public const string TableCoefficient = "Coefficient";
    public const string TableOperationLog = "OperationLog";

    /// <summary>数据表下拉选项（Key = Table* 常量）。</summary>
    public sealed record TableOption(string Key, string DisplayName);

    /// <summary>可切换的数据表（标准值/控制限可删除；校正系数版本化与操作日志审计留痕，只读）。</summary>
    public IReadOnlyList<TableOption> TableOptions { get; } = new TableOption[]
    {
        new(TableSample, "样品台账（SCT_Sample）"),
        new(TableCertifiedValue, "标准值（SCT_CertifiedValue）"),
        new(TableControlLimit, "控制限（ControlLimit）"),
        new(TableCoefficient, "校正系数（CorrectionCoefficient）"),
        new(TableOperationLog, "操作日志（SCT_OperationLog）"),
    };

    private readonly ISctSampleRepository _samples;
    private readonly ISctCertifiedValueRepository _certRepo;
    private readonly IControlLimitRepository _limitRepo;
    private readonly ICorrectionCoefficientRepository _coeffRepo;
    private readonly ISctOperationLogRepository _logRepo;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IErrorCollector _errors;

    /// <summary>全部台账行（非生产样品），未筛选。</summary>
    private List<SampleRowItem> _allSctRows = new();

    /// <summary>子表全量行缓存（未筛选；分页/搜索在 RefreshView 统一切片）。</summary>
    private List<CertifiedValueRowItem> _allCertified = new();
    private List<ControlLimitRowItem> _allLimits = new();
    private List<CoefficientRowItem> _allCoeffs = new();
    private List<OperationLogRowItem> _allLogs = new();

    [ObservableProperty]
    private ObservableCollection<SampleRowItem> _sctSamples = new();

    /// <summary>当前展示的数据表（默认样品台账；仅样品表显示录入/筛选控件）。
    /// 变化即触发该表数据加载（ComboBox 双向绑定直接改此属性，也走同一入口）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSampleTable))]
    [NotifyPropertyChangedFor(nameof(ActiveTableDisplay))]
    private string _activeTable = TableSample;

    /// <summary>当前激活表的进行中加载任务（切表后可 await 确定性等待；初始为已完成）。</summary>
    private Task _activeTableLoad = Task.CompletedTask;

    partial void OnActiveTableChanged(string value)
    {
        CurrentPage = 1;                        // 切表回到第 1 页
        _activeTableLoad = LoadActiveTableAsync();
    }

    /// <summary>按当前激活表加载对应数据（无 IsLoading 防抖，供切表路径直接调用）。</summary>
    private async Task LoadActiveTableAsync()
    {
        switch (ActiveTable)
        {
            case TableCertifiedValue:
                await LoadCertifiedValuesAsync();
                break;
            case TableControlLimit:
                await LoadControlLimitsAsync();
                break;
            case TableCoefficient:
                await LoadCoefficientsAsync();
                break;
            case TableOperationLog:
                await LoadOperationLogsAsync();
                break;
            default:
                await LoadSamplesAsync();
                break;
        }
    }

    /// <summary>是否处于样品台账表（控制录入按钮/类型筛选/状态筛选/注册表单的显隐）。</summary>
    public bool IsSampleTable => ActiveTable == TableSample;

    /// <summary>当前数据表显示名（卡片标题随切表更新）。</summary>
    public string ActiveTableDisplay => TableOptions.First(o => o.Key == ActiveTable).DisplayName;

    [ObservableProperty]
    private ObservableCollection<CertifiedValueRowItem> _certifiedValues = new();

    [ObservableProperty]
    private ObservableCollection<ControlLimitRowItem> _controlLimitRows = new();

    [ObservableProperty]
    private ObservableCollection<CoefficientRowItem> _coefficientRows = new();

    [ObservableProperty]
    private ObservableCollection<OperationLogRowItem> _operationLogRows = new();

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
    public const int PageSize = 20;

    /// <summary>当前页码（从 1 起）。</summary>
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

    /// <summary>正在加载标记（防重复加载）。</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>删除失败提示（空即无错误；如数据库约束冲突）。</summary>
    [ObservableProperty]
    private string? _deleteError;

    // ==================== 样品注册（任务清单"入库初始化"） ====================

    /// <summary>样品类型下拉选项（类型键：设置样/控制样/类型标准化样/生产样）。
    /// 必须为实例属性供 XAML ItemsSource 绑定（静态字段不可绑定）。</summary>
    public IReadOnlyList<string> SampleTypeOptions { get; } = new[]
    {
        SampleType.SettingUp, SampleType.Control, SampleType.TypeStd, SampleType.Production,
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
            // 入库后刷新列表（await 确保刷新完成，调用方 RegisterAsync 本身异步）
            await LoadCommand.ExecuteAsync(null);
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

    public SamplesViewModel(
        ISctSampleRepository samples,
        ISctCertifiedValueRepository certifiedValues,
        IControlLimitRepository controlLimits,
        ICorrectionCoefficientRepository coefficients,
        ISctOperationLogRepository operationLogs,
        IUiDispatcher ui,
        IDialogService dialogs,
        IErrorCollector errors)
    {
        _samples = samples;
        _certRepo = certifiedValues;
        _limitRepo = controlLimits;
        _coeffRepo = coefficients;
        _logRepo = operationLogs;
        _ui = ui;
        _dialogs = dialogs;
        _errors = errors;
    }

    /// <summary>导航进入时加载（导航路径为 fire-and-forget；测试可 await LoadCommand.ExecuteAsync 保证确定性）。</summary>
    public void OnNavigatedTo() => LoadCommand.Execute(null);

    /// <summary>从数据库加载当前激活数据表（I/O 在后台线程，结果经 IUiDispatcher 回 UI）。</summary>
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
            await LoadActiveTableAsync();
        }
        finally
        {
            _ui.Invoke(() => IsLoading = false);
        }
    }

    /// <summary>加载样品台账（含生产样，供类型筛选切换查看）。</summary>
    private async Task LoadSamplesAsync()
    {
        var rows = await Task.Run(() => _samples.GetAll().Select(MapRow).ToList());
        _ui.Invoke(() =>
        {
            _allSctRows = rows.ToList();
            ApplyFilter(FilterKey);
        });
    }

    /// <summary>加载标准值表（全量入缓存后统一分页/搜索视图）。</summary>
    private async Task LoadCertifiedValuesAsync()
    {
        var rows = await Task.Run(() => _certRepo.GetAll().Select(s => new CertifiedValueRowItem
        {
            Id = s.Id,
            SampleId = s.SampleId,
            Element = s.Element,
            ValueText = s.CertifiedValue.ToString("0.####"),
            Unit = s.Unit,
            UncertaintyText = s.Uncertainty?.ToString("0.####") ?? "—",
        }).ToList());
        _ui.Invoke(() =>
        {
            _allCertified = rows;
            RefreshView();
        });
    }

    /// <summary>加载控制限表（全量入缓存后统一分页/搜索视图）。</summary>
    private async Task LoadControlLimitsAsync()
    {
        var rows = await Task.Run(() => _limitRepo.GetAll().Select(l => new ControlLimitRowItem
        {
            LimitId = l.LimitId,
            SampleId = l.SampleId,
            Element = l.Element,
            UpperText = l.UpperLimit.ToString("0.####"),
            LowerText = l.LowerLimit.ToString("0.####"),
            LimitTypeText = l.LimitType switch
            {
                "Absolute" => "绝对限值",
                "Percent" => "百分比",
                "Sigma" => "σ 偏差",
                _ => l.LimitType,
            },
        }).ToList());
        _ui.Invoke(() =>
        {
            _allLimits = rows;
            RefreshView();
        });
    }

    /// <summary>加载校正系数表（全量入缓存后统一分页/搜索视图）。</summary>
    private async Task LoadCoefficientsAsync()
    {
        var rows = await Task.Run(() => _coeffRepo.GetAll().Select(c => new CoefficientRowItem
        {
            CoeffId = c.CoeffId,
            MethodId = c.MethodId,
            Element = c.Element,
            SlopeText = c.Slope.ToString("0.######"),
            InterceptText = c.Intercept.ToString("0.######"),
            EffectiveTimeText = c.EffectiveTime,
            SourceSampleText = c.SourceSampleId ?? "—",
            CurrentText = c.IsCurrent ? "✓ 当前有效" : "历史版本",
            ReferenceElementText = c.ReferenceElement ?? "—",
            VersionText = $"v{c.Version}",
        }).ToList());
        _ui.Invoke(() =>
        {
            _allCoeffs = rows;
            RefreshView();
        });
    }

    /// <summary>加载操作日志表（按时间倒序，全量入缓存后统一分页/搜索视图）。</summary>
    private async Task LoadOperationLogsAsync()
    {
        var rows = await Task.Run(() => _logRepo.GetAll().Select(l => new OperationLogRowItem
        {
            LogId = l.LogId,
            SampleIdText = l.SampleId ?? "—",
            OperationTypeText = l.OperationType switch
            {
                "Standardization" => "标准化",
                "ControlCheck" => "控制检查",
                "HeightMeasure" => "测高",
                _ => l.OperationType,
            },
            TriggerReasonText = l.TriggerReason ?? "—",
            OperationTimeText = l.OperationTime,
            ResultStatusText = l.ResultStatus switch
            {
                "Success" => "● 成功",
                "Warning" => "▲ 预警",
                "Fail" => "✖ 失败",
                _ => l.ResultStatus,
            },
        }).ToList());
        _ui.Invoke(() =>
        {
            _allLogs = rows;
            RefreshView();
        });
    }

    /// <summary>切换数据表：仅接受 TableOptions 中登记的键；设置属性触发加载并等待其完成（确定性）。</summary>
    [RelayCommand]
    private async Task SetTableAsync(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || TableOptions.All(o => o.Key != key))
        {
            return;
        }

        if (ActiveTable == key)
        {
            return;
        }

        ActiveTable = key;
        await _activeTableLoad;
    }

    /// <summary>删除标准值（弹窗二次确认；失败经统一错误链上报）。</summary>
    [RelayCommand]
    private async Task DeleteCertifiedValueAsync(CertifiedValueRowItem? row)
    {
        if (row is null || IsLoading)
        {
            return;
        }

        DeleteError = null;
        if (!_dialogs.Confirm("删除标准值", $"确认删除样品「{row.SampleId}」元素「{row.Element}」的标准值？"))
        {
            return;
        }

        try
        {
            await Task.Run(() => _certRepo.Delete(row.Id));
            await LoadCertifiedValuesAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            DeleteError = $"删除失败：{ex.Message}";
            _errors.Collect(ErrorCodes.SampleDeleteFailed, "标准值删除失败", row.SampleId, ex);
        }
    }

    /// <summary>删除控制限（弹窗二次确认；失败经统一错误链上报）。</summary>
    [RelayCommand]
    private async Task DeleteControlLimitAsync(ControlLimitRowItem? row)
    {
        if (row is null || IsLoading)
        {
            return;
        }

        DeleteError = null;
        if (!_dialogs.Confirm("删除控制限", $"确认删除样品「{row.SampleId}」元素「{row.Element}」的控制限？\n删除后不可恢复。"))
        {
            return;
        }

        try
        {
            await Task.Run(() => _limitRepo.Delete(row.LimitId));
            await LoadControlLimitsAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            DeleteError = $"删除失败：{ex.Message}";
            _errors.Collect(ErrorCodes.SampleDeleteFailed, "控制限删除失败", row.SampleId, ex);
        }
    }

    /// <summary>删除校正系数版本（弹窗二次确认；删除破坏版本化历史，文案注明不可恢复）。</summary>
    [RelayCommand]
    private async Task DeleteCoefficientAsync(CoefficientRowItem? row)
    {
        if (row is null || IsLoading)
        {
            return;
        }

        DeleteError = null;
        if (!_dialogs.Confirm("删除校正系数", $"确认删除「{row.MethodId}」元素「{row.Element}」的 {row.VersionText} 系数版本？\n删除后不可恢复，将破坏版本化历史。"))
        {
            return;
        }

        try
        {
            await Task.Run(() => _coeffRepo.Delete(row.CoeffId));
            await LoadCoefficientsAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            DeleteError = $"删除失败：{ex.Message}";
            _errors.Collect(ErrorCodes.SampleDeleteFailed, "校正系数删除失败", null, ex);
        }
    }

    /// <summary>删除操作日志（弹窗二次确认；删除破坏审计留痕，文案注明不可恢复）。</summary>
    [RelayCommand]
    private async Task DeleteOperationLogAsync(OperationLogRowItem? row)
    {
        if (row is null || IsLoading)
        {
            return;
        }

        DeleteError = null;
        if (!_dialogs.Confirm("删除操作日志", $"确认删除「{row.SampleIdText}」的操作日志（{row.OperationTypeText} {row.OperationTimeText}）？\n删除后不可恢复，将破坏审计留痕。"))
        {
            return;
        }

        try
        {
            await Task.Run(() => _logRepo.Delete(row.LogId));
            await LoadOperationLogsAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            DeleteError = $"删除失败：{ex.Message}";
            _errors.Collect(ErrorCodes.SampleDeleteFailed, "操作日志删除失败", null, ex);
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

    /// <summary>搜索命令：关键词变化后回到第 1 页刷新（回车/按钮触发，对当前激活表生效）。</summary>
    [RelayCommand]
    private void Search() => RefreshView(resetPage: true);

    /// <summary>筛选 + 关键词过滤 + 分页切片统一入口（按当前激活表分发到对应集合）。</summary>
    private void RefreshView(bool resetPage = false)
    {
        var keyword = SearchText?.Trim();
        switch (ActiveTable)
        {
            case TableCertifiedValue:
                ApplyPagedView(_allCertified, r => Matches(r.SampleId, keyword) || Matches(r.Element, keyword), resetPage,
                    page => CertifiedValues = new ObservableCollection<CertifiedValueRowItem>(page));
                break;
            case TableControlLimit:
                ApplyPagedView(_allLimits, r => Matches(r.SampleId, keyword) || Matches(r.Element, keyword), resetPage,
                    page => ControlLimitRows = new ObservableCollection<ControlLimitRowItem>(page));
                break;
            case TableCoefficient:
                ApplyPagedView(_allCoeffs, r => Matches(r.MethodId, keyword) || Matches(r.Element, keyword) || Matches(r.SourceSampleText, keyword), resetPage,
                    page => CoefficientRows = new ObservableCollection<CoefficientRowItem>(page));
                break;
            case TableOperationLog:
                ApplyPagedView(_allLogs, r => Matches(r.SampleIdText, keyword) || Matches(r.OperationTypeText, keyword) || Matches(r.TriggerReasonText, keyword), resetPage,
                    page => OperationLogRows = new ObservableCollection<OperationLogRowItem>(page));
                break;
            default:
                // 样品台账：类型筛选 + 状态筛选 + 关键词
                var filtered = FilterKey == FilterAll
                    ? _allSctRows
                    : _allSctRows.Where(r => r.SampleTypeKey == FilterKey);
                if (!string.IsNullOrEmpty(StatusFilter?.Key))
                {
                    filtered = filtered.Where(r => r.Status == StatusFilter.Key);
                }

                ApplyPagedView(filtered.ToList(), r => Matches(r.SampleId, keyword) || Matches(r.SampleName, keyword), resetPage,
                    page => SctSamples = new ObservableCollection<SampleRowItem>(page));
                break;
        }
    }

    /// <summary>通用分页切片：统计总量/页数 → 收敛页码 → 输出当前页行。</summary>
    private void ApplyPagedView<T>(IReadOnlyList<T> source, Func<T, bool>? predicate, bool resetPage, Action<List<T>> sink)
    {
        IEnumerable<T> query = source;
        if (predicate is not null)
        {
            query = query.Where(predicate);
        }

        var list = query.ToList();
        TotalCount = list.Count;
        TotalPages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)PageSize));
        if (resetPage || CurrentPage > TotalPages)
        {
            CurrentPage = resetPage ? 1 : TotalPages;
        }

        sink(list.Skip((CurrentPage - 1) * PageSize).Take(PageSize).ToList());
    }

    /// <summary>关键词模糊匹配（忽略大小写；空关键词恒命中；"—" 占位不参与命中）。</summary>
    private static bool Matches(string? text, string? keyword)
    {
        if (string.IsNullOrEmpty(keyword))
        {
            return true;
        }

        return !string.IsNullOrEmpty(text)
            && text != "—"
            && text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>回首页（已在第 1 页时禁用）。</summary>
    [RelayCommand(CanExecute = nameof(HasPrevPage))]
    private void FirstPage()
    {
        CurrentPage = 1;
        RefreshView();
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
                ApplyFilter(FilterKey);
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
            SampleType.Production => "生产样",
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
