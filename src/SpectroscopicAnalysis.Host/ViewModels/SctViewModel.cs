using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.SctStrategy;
using SpectroscopicAnalysis.Host.Ui;

namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>
/// SCT 标样库行展示模型（领域数据 → 展示文本映射，开发规范 3.3）。
/// Status 为原始枚举串（视图 DataTrigger 切换徽章颜色），与样品档案页 SampleRowItem 同模式。
/// </summary>
public sealed class SctRowItem
{
    public required string SampleId { get; init; }
    public required string SampleName { get; init; }
    /// <summary>类型键（筛选用，非绑定字段）。</summary>
    public required string SampleTypeKey { get; init; }
    public required string TypeDisplay { get; init; }
    public string? AlloyType { get; init; }
    public string? RackPosition { get; init; }
    public required string ThicknessText { get; init; }
    public required string MinThicknessText { get; init; }
    public required string UsageText { get; init; }
    /// <summary>原始状态枚举串（视图 DataTrigger 切换徽章颜色）。</summary>
    public required string Status { get; init; }
    public required string StatusText { get; init; }
    public required string LastHeightText { get; init; }
    public required string LastUseText { get; init; }
    public required bool IsActive { get; init; }
    public required string ActiveText { get; init; }
}

/// <summary>SCT 操作日志行展示模型（含厚度/系数前后值、合格判定；比样品档案页的日志行更全）。</summary>
public sealed class SctLogRowItem
{
    public required string TimeText { get; init; }
    public required string SampleIdText { get; init; }
    public required string OperationTypeText { get; init; }
    /// <summary>原始操作类型串（徽章着色）。</summary>
    public required string OperationType { get; init; }
    public required string TriggerReasonText { get; init; }
    public required string ThicknessText { get; init; }
    public required string CoeffText { get; init; }
    public required string PassText { get; init; }
    /// <summary>原始结果状态串（徽章着色）。</summary>
    public required string ResultStatus { get; init; }
    public required string ResultStatusText { get; init; }
}

/// <summary>
/// SCT 管理 ViewModel：状态监控概览 + 标样库运行维护（测高登记/阈值调整/启用禁用）+ 执行记录 + 策略配置。
/// 设计依据 docs/SCT管理页面设计.md；数据层经仓储抽象，I/O 走 Task.Run + IUiDispatcher（开发规范第 4 节）。
/// 手动触发（标准化/控制检查/测高执行）为策略引擎挂点，命令禁用待 SctStrategyEngine 接入。
/// </summary>
public sealed partial class SctViewModel : ObservableObject, IPageLifecycle
{
    public const string FilterAll = "All";

    /// <summary>类型筛选选项（Key = SampleType 常量；必须实例属性供 XAML 绑定）。</summary>
    public sealed record FilterOption(string Key, string DisplayName);

    public IReadOnlyList<FilterOption> FilterOptions { get; } = new FilterOption[]
    {
        new(FilterAll, "全部"),
        new(SampleType.SettingUp, "设置样"),
        new(SampleType.Control, "控制样"),
        new(SampleType.TypeStd, "类型标准化样"),
    };

    /// <summary>控制样插入模式选项（SctPolicyOptions.ControlInsertMode）。</summary>
    public IReadOnlyList<string> InsertModeOptions { get; } = new[]
    {
        ControlInsertMode.ByCount, ControlInsertMode.ByTime,
    };

    /// <summary>控制样判定限类型选项（与 ControlLimit.LimitType 同名）。</summary>
    public IReadOnlyList<string> LimitTypeOptions { get; } = new[] { "Absolute", "Percent", "Sigma" };

    private readonly ISctSampleRepository _samples;
    private readonly ISctOperationLogRepository _logs;
    private readonly ISctPolicyStore _policyStore;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IErrorCollector _errors;

    /// <summary>策略引擎（手动触发提交任务；未注入时按钮保持禁用——测试兼容）。</summary>
    private readonly ISctStrategyEngine? _strategyEngine;

    /// <summary>全部 SCT 标样（非生产样），未筛选。</summary>
    private List<SctRowItem> _allRows = new();

    /// <summary>全部操作日志（时间倒序），未筛选。</summary>
    private List<SctLogRowItem> _allLogRows = new();

    public SctViewModel(
        ISctSampleRepository samples,
        ISctOperationLogRepository logs,
        ISctPolicyStore policyStore,
        IUiDispatcher ui,
        IDialogService dialogs,
        IErrorCollector errors,
        ISctStrategyEngine? strategyEngine = null)
    {
        _samples = samples;
        _logs = logs;
        _policyStore = policyStore;
        _ui = ui;
        _dialogs = dialogs;
        _errors = errors;
        _strategyEngine = strategyEngine;
        LoadPolicyToForm();
    }

    // ==================== 状态监控概览 ====================

    [ObservableProperty]
    private int _settingUpCount;

    [ObservableProperty]
    private int _controlCount;

    [ObservableProperty]
    private int _typeStdCount;

    [ObservableProperty]
    private int _needMaintenanceCount;

    /// <summary>最近一次标准化摘要（样品 + 时间 + 结果）。</summary>
    [ObservableProperty]
    private string _lastStandardizationSummary = "—（暂无记录）";

    /// <summary>最近一次控制检查摘要（样品 + 时间 + 结果）。</summary>
    [ObservableProperty]
    private string _lastControlCheckSummary = "—（暂无记录）";

    // ==================== 标样库列表：筛选 / 搜索 / 分页 ====================

    [ObservableProperty]
    private string _filterKey = FilterAll;

    partial void OnFilterKeyChanged(string value) => RefreshSampleView(resetPage: true);

    /// <summary>搜索关键词（编号/名称/合金/架位模糊匹配；空即不过滤）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    private string _searchText = string.Empty;

    /// <summary>是否已输入搜索关键词（控制搜索框水印显隐）。</summary>
    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    [ObservableProperty]
    private ObservableCollection<SctRowItem> _sctSamples = new();

    /// <summary>选中行（测高/阈值/启禁用的操作对象）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenMeasureCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyMeasureCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenThresholdCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveThresholdsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleActiveCommand))]
    [NotifyCanExecuteChangedFor(nameof(TriggerHeightMeasureCommand))]
    private SctRowItem? _selectedSample;

    /// <summary>标样库分页大小（每页 10 行，完整展示不内部滚动）。</summary>
    public const int PageSize = 10;

    /// <summary>执行记录分页大小（每页 10 行，完整展示不内部滚动）。</summary>
    public const int LogPageSize = 10;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageDisplay))]
    [NotifyPropertyChangedFor(nameof(HasPrevPage))]
    [NotifyPropertyChangedFor(nameof(HasNextPage))]
    [NotifyCanExecuteChangedFor(nameof(FirstPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private int _currentPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageDisplay))]
    [NotifyCanExecuteChangedFor(nameof(FirstPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private int _totalPages = 1;

    [ObservableProperty]
    private int _totalCount;

    public string PageDisplay => $"第 {CurrentPage} / {TotalPages} 页";

    public bool HasPrevPage => CurrentPage > 1;

    public bool HasNextPage => CurrentPage < TotalPages;

    // ==================== 执行记录（独立分页） ====================

    [ObservableProperty]
    private ObservableCollection<SctLogRowItem> _operationRows = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogPageDisplay))]
    [NotifyPropertyChangedFor(nameof(HasLogPrevPage))]
    [NotifyPropertyChangedFor(nameof(HasLogNextPage))]
    [NotifyCanExecuteChangedFor(nameof(LogPrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(LogNextPageCommand))]
    private int _logCurrentPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogPageDisplay))]
    [NotifyCanExecuteChangedFor(nameof(LogPrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(LogNextPageCommand))]
    private int _logTotalPages = 1;

    public string LogPageDisplay => $"第 {LogCurrentPage} / {LogTotalPages} 页";

    public bool HasLogPrevPage => LogCurrentPage > 1;

    public bool HasLogNextPage => LogCurrentPage < LogTotalPages;

    // ==================== 测高登记（PDF 3.2 状态流转） ====================

    [ObservableProperty]
    private bool _isMeasureOpen;

    [ObservableProperty]
    private string _measureThicknessText = string.Empty;

    /// <summary>测高登记错误提示（空即无错误）。</summary>
    [ObservableProperty]
    private string? _measureError;

    // ==================== 阈值调整（最小厚度 / 最大使用次数） ====================

    [ObservableProperty]
    private bool _isThresholdOpen;

    [ObservableProperty]
    private string _editMinThicknessText = string.Empty;

    [ObservableProperty]
    private string _editMaxUsageText = string.Empty;

    /// <summary>阈值调整错误提示（空即无错误）。</summary>
    [ObservableProperty]
    private string? _thresholdError;

    // ==================== 策略配置（SctPolicyOptions 表单绑定） ====================

    /// <summary>设置样选择规则选项（任务清单第 5 节②）。</summary>
    public IReadOnlyList<string> SettingSelectionModeOptions { get; } = new[]
    {
        SettingSampleSelectionMode.LeastUsed, SettingSampleSelectionMode.RoundRobin
    };

    [ObservableProperty]
    private string _policySettingSelectionMode = SettingSampleSelectionMode.LeastUsed;

    [ObservableProperty]
    private bool _policyRotationEnabled = true;

    [ObservableProperty]
    private bool _policyMatchByAlloy = true;

    [ObservableProperty]
    private bool _policyTypeOnAlloySwitch = true;

    [ObservableProperty]
    private bool _policyTypePeriodEnabled;

    [ObservableProperty]
    private string _policyTypePeriodDays = "7";

    /// <summary>控制样超差闭环验证重测次数上限（文本承接，保存时校验）。</summary>
    [ObservableProperty]
    private string _policyRecheckMax = "1";

    [ObservableProperty]
    private string _policyInsertMode = ControlInsertMode.ByCount;

    [ObservableProperty]
    private string _policyInsertCount = "15";

    [ObservableProperty]
    private string _policyInsertMinutes = "60";

    [ObservableProperty]
    private string _policyLimitType = "Sigma";

    [ObservableProperty]
    private string _policyLimitValue = "2";

    [ObservableProperty]
    private bool _autoOnControlFail = true;

    [ObservableProperty]
    private bool _standardizeOnAutoMode = true;

    [ObservableProperty]
    private bool _periodicEnabled;

    [ObservableProperty]
    private string _periodicHours = "8";

    /// <summary>策略保存成功提示（空即无提示）。</summary>
    [ObservableProperty]
    private string? _policySavedMessage;

    /// <summary>策略配置错误提示（空即无错误）。</summary>
    [ObservableProperty]
    private string? _policyError;

    /// <summary>导航进入时加载（测试可 await LoadCommand.ExecuteAsync 保证确定性）。</summary>
    public void OnNavigatedTo() => LoadCommand.Execute(null);

    /// <summary>加载标样库与执行记录（I/O 后台线程，结果回 UI 线程）。</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        var (rows, logRows) = await Task.Run(() =>
        {
            var samples = _samples.GetAll()
                .Where(s => s.SampleType != SampleType.Production)
                .Select(MapRow)
                .ToList();
            var logs = _logs.GetAll().Select(MapLogRow).ToList();
            return (samples, logs);
        });

        _ui.Invoke(() =>
        {
            _allRows = rows;
            _allLogRows = logRows;
            RefreshOverview();
            RefreshSampleView(resetPage: true);
            RefreshLogView(resetPage: true);
        });
    }

    /// <summary>概览统计与最近操作摘要（数据来自已加载缓存）。</summary>
    private void RefreshOverview()
    {
        SettingUpCount = _allRows.Count(r => r.SampleTypeKey == SampleType.SettingUp);
        ControlCount = _allRows.Count(r => r.SampleTypeKey == SampleType.Control);
        TypeStdCount = _allRows.Count(r => r.SampleTypeKey == SampleType.TypeStd);
        NeedMaintenanceCount = _allRows.Count(r => r.Status is SampleStatus.NeedMeasure or SampleStatus.Disabled);

        LastStandardizationSummary = SummarizeLast("Standardization");
        LastControlCheckSummary = SummarizeLast("ControlCheck");
    }

    /// <summary>取指定操作类型的最新一条日志生成摘要（缓存已按时间倒序）。</summary>
    private string SummarizeLast(string operationType)
    {
        var last = _allLogRows.FirstOrDefault(r => r.OperationType == operationType);
        return last is null ? "—（暂无记录）" : $"{last.SampleIdText} · {last.TimeText} · {last.ResultStatusText}";
    }

    /// <summary>标样库筛选 + 关键词 + 分页切片（与样品档案页 ApplyPagedView 同思路）。</summary>
    private void RefreshSampleView(bool resetPage = false)
    {
        var keyword = SearchText?.Trim();
        var filtered = _allRows
            .Where(r => FilterKey == FilterAll || r.SampleTypeKey == FilterKey)
            .Where(r => Matches(r.SampleId, keyword) || Matches(r.SampleName, keyword)
                || Matches(r.AlloyType, keyword) || Matches(r.RackPosition, keyword))
            .ToList();

        TotalCount = filtered.Count;
        TotalPages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)PageSize));
        if (resetPage || CurrentPage > TotalPages)
        {
            CurrentPage = resetPage ? 1 : TotalPages;
        }

        SctSamples = new ObservableCollection<SctRowItem>(
            filtered.Skip((CurrentPage - 1) * PageSize).Take(PageSize));
    }

    /// <summary>执行记录分页切片（缓存已倒序）。</summary>
    private void RefreshLogView(bool resetPage = false)
    {
        LogTotalPages = Math.Max(1, (int)Math.Ceiling(_allLogRows.Count / (double)LogPageSize));
        if (resetPage || LogCurrentPage > LogTotalPages)
        {
            LogCurrentPage = resetPage ? 1 : LogTotalPages;
        }

        OperationRows = new ObservableCollection<SctLogRowItem>(
            _allLogRows.Skip((LogCurrentPage - 1) * LogPageSize).Take(LogPageSize));
    }

    /// <summary>关键词模糊匹配（忽略大小写；空关键词恒命中）。</summary>
    private static bool Matches(string? text, string? keyword)
    {
        if (string.IsNullOrEmpty(keyword))
        {
            return true;
        }

        return !string.IsNullOrEmpty(text) && text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>类型筛选切换（ModeBtn 组入口；属性变化回调触发刷新）。</summary>
    [RelayCommand]
    private void SetFilter(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || FilterOptions.All(o => o.Key != key))
        {
            return;
        }

        FilterKey = key;
    }

    /// <summary>查询命令：按当前关键词回到第 1 页刷新。</summary>
    [RelayCommand]
    private void Search() => RefreshSampleView(resetPage: true);

    [RelayCommand(CanExecute = nameof(HasPrevPage))]
    private void FirstPage()
    {
        CurrentPage = 1;
        RefreshSampleView();
    }

    [RelayCommand(CanExecute = nameof(HasPrevPage))]
    private void PrevPage()
    {
        CurrentPage--;
        RefreshSampleView();
    }

    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private void NextPage()
    {
        CurrentPage++;
        RefreshSampleView();
    }

    /// <summary>标样库页码输入框（跳页用；解析失败忽略不清空，便于修正）。</summary>
    [ObservableProperty]
    private string _pageJumpInput = string.Empty;

    /// <summary>标样库页脚「跳转」：解析输入页码直达（越界收敛到有效范围）。</summary>
    [RelayCommand]
    private void JumpToPage()
    {
        if (!int.TryParse(PageJumpInput?.Trim(), out var page))
        {
            return;
        }

        CurrentPage = Math.Clamp(page, 1, TotalPages);
        RefreshSampleView();
        PageJumpInput = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(HasLogPrevPage))]
    private void LogPrevPage()
    {
        LogCurrentPage--;
        RefreshLogView();
    }

    [RelayCommand(CanExecute = nameof(HasLogNextPage))]
    private void LogNextPage()
    {
        LogCurrentPage++;
        RefreshLogView();
    }

    /// <summary>执行记录页码输入框（跳页用；解析失败忽略不清空，便于修正）。</summary>
    [ObservableProperty]
    private string _logPageJumpInput = string.Empty;

    /// <summary>执行记录页脚「跳转」：解析输入页码直达（越界收敛到有效范围）。</summary>
    [RelayCommand]
    private void LogJumpToPage()
    {
        if (!int.TryParse(LogPageJumpInput?.Trim(), out var page))
        {
            return;
        }

        LogCurrentPage = Math.Clamp(page, 1, LogTotalPages);
        RefreshLogView();
        LogPageJumpInput = string.Empty;
    }

    // ==================== 测高登记 ====================

    /// <summary>打开测高登记面板（需选中行）。</summary>
    [RelayCommand(CanExecute = nameof(CanMaintain))]
    private void OpenMeasure()
    {
        MeasureThicknessText = string.Empty;
        MeasureError = null;
        IsThresholdOpen = false;
        IsMeasureOpen = true;
    }

    /// <summary>取消测高登记。</summary>
    [RelayCommand]
    private void CancelMeasure()
    {
        MeasureThicknessText = string.Empty;
        MeasureError = null;
        IsMeasureOpen = false;
    }

    /// <summary>
    /// 提交测高登记：按状态机流转（PDF 3.2）——低于最小厚度 → Disabled（报废阈值）；
    /// 否则 → Normal（清 NeedMeasure），更新上次测高时间，写 HeightMeasure 审计日志。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMaintain))]
    private async Task ApplyMeasureAsync()
    {
        var row = SelectedSample;
        if (row is null)
        {
            return;
        }

        if (!double.TryParse(MeasureThicknessText, out var thickness) || thickness <= 0)
        {
            MeasureError = "实测厚度必须为大于 0 的数值";
            return;
        }

        var sampleId = row.SampleId;
        var result = await Task.Run<(SctSample? Sample, string? Error)>(() =>
        {
            var sample = _samples.GetById(sampleId);
            if (sample is null)
            {
                return (Sample: null, Error: (string?)$"样品不存在：{sampleId}");
            }

            var thicknessBefore = sample.CurrentThickness;
            var newStatus = thickness < sample.MinThickness ? SampleStatus.Disabled : SampleStatus.Normal;
            sample.CurrentThickness = thickness;
            sample.Status = newStatus;
            sample.LastHeightCheckTime = DateTime.Now;
            _samples.Update(sample);
            _logs.Insert(new SctOperationLog
            {
                SampleId = sample.SampleId,
                OperationType = "HeightMeasure",
                TriggerReason = "手动测高登记",
                ThicknessBefore = thicknessBefore,
                ThicknessAfter = thickness,
                OperationTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ResultStatus = "Success",
            });
            return (Sample: sample, Error: (string?)null);
        });

        if (result.Error is not null)
        {
            MeasureError = result.Error;
            _errors.Collect(ErrorCodes.SctSampleMaintainFailed, result.Error, sampleId);
            return;
        }

        _ui.Invoke(() =>
        {
            MeasureThicknessText = string.Empty;
            MeasureError = null;
            IsMeasureOpen = false;
        });
        await LoadCommand.ExecuteAsync(null);
    }

    // ==================== 阈值调整 ====================

    /// <summary>打开阈值调整面板（预填选中行当前值）。</summary>
    [RelayCommand(CanExecute = nameof(CanMaintain))]
    private void OpenThreshold()
    {
        var row = SelectedSample;
        if (row is null)
        {
            return;
        }

        EditMinThicknessText = row.MinThicknessText;
        EditMaxUsageText = row.UsageText.Split('/')[^1].Trim();
        ThresholdError = null;
        IsMeasureOpen = false;
        IsThresholdOpen = true;
    }

    /// <summary>取消阈值调整。</summary>
    [RelayCommand]
    private void CancelThreshold()
    {
        ThresholdError = null;
        IsThresholdOpen = false;
    }

    /// <summary>保存阈值（二次确认后写库；低于新阈值立即标记 Disabled）。</summary>
    [RelayCommand(CanExecute = nameof(CanMaintain))]
    private async Task SaveThresholdsAsync()
    {
        var row = SelectedSample;
        if (row is null)
        {
            return;
        }

        if (!double.TryParse(EditMinThicknessText, out var min) || min <= 0)
        {
            ThresholdError = "最小厚度必须为大于 0 的数值";
            return;
        }

        if (!int.TryParse(EditMaxUsageText, out var maxUsage) || maxUsage < 1)
        {
            ThresholdError = "最大使用次数必须为不小于 1 的整数";
            return;
        }

        var sampleId = row.SampleId;
        if (!_dialogs.Confirm("调整阈值", $"确认调整标样 {sampleId} 的最小厚度 / 最大使用次数？"))
        {
            return;
        }

        var error = await Task.Run(() =>
        {
            var sample = _samples.GetById(sampleId);
            if (sample is null)
            {
                return (string?)$"样品不存在：{sampleId}";
            }

            sample.MinThickness = min;
            sample.MaxUsageCount = maxUsage;
            // 阈值变化可能影响状态：低于新阈值立即标记 Disabled；恢复需测高登记或更换样品（人工）
            if (sample.CurrentThickness < min)
            {
                sample.Status = SampleStatus.Disabled;
            }

            _samples.Update(sample);
            return (string?)null;
        });

        if (error is not null)
        {
            ThresholdError = error;
            _errors.Collect(ErrorCodes.SctSampleMaintainFailed, error, sampleId);
            return;
        }

        _ui.Invoke(() =>
        {
            ThresholdError = null;
            IsThresholdOpen = false;
        });
        await LoadCommand.ExecuteAsync(null);
    }

    // ==================== 启用 / 禁用（危险操作，二次确认） ====================

    [RelayCommand(CanExecute = nameof(CanMaintain))]
    private async Task ToggleActiveAsync()
    {
        var row = SelectedSample;
        if (row is null)
        {
            return;
        }

        var sampleId = row.SampleId;
        var disable = row.IsActive;
        var title = disable ? "禁用标样" : "启用标样";
        var message = disable
            ? $"确认禁用标样 {sampleId}？禁用后策略引擎将不再选用该样品。"
            : $"确认启用标样 {sampleId}？";
        if (!_dialogs.Confirm(title, message))
        {
            return;
        }

        var error = await Task.Run(() =>
        {
            var sample = _samples.GetById(sampleId);
            if (sample is null)
            {
                return (string?)$"样品不存在：{sampleId}";
            }

            sample.IsActive = !disable;
            _samples.Update(sample);
            return (string?)null;
        });

        if (error is not null)
        {
            _errors.Collect(ErrorCodes.SctSampleMaintainFailed, error, sampleId);
            return;
        }

        await LoadCommand.ExecuteAsync(null);
    }

    /// <summary>维护类命令可用性：需选中行。</summary>
    private bool CanMaintain() => SelectedSample is not null;

    // ==================== 策略配置 ====================

    /// <summary>把已加载配置填充到表单属性（构造时调用一次）。</summary>
    private void LoadPolicyToForm()
    {
        var p = _policyStore.Load();
        PolicySettingSelectionMode = p.SettingSampleSelectionMode;
        PolicyRotationEnabled = p.ControlRotationEnabled;
        PolicyMatchByAlloy = p.ControlMatchByAlloy;
        PolicyTypeOnAlloySwitch = p.TypeStandardizeOnAlloySwitch;
        PolicyTypePeriodEnabled = p.TypeStandardizePeriodEnabled;
        PolicyTypePeriodDays = p.TypeStandardizePeriodDays.ToString();
        PolicyRecheckMax = p.ControlRecheckMaxCount.ToString();
        PolicyInsertMode = p.ControlInsertMode;
        PolicyInsertCount = p.ControlInsertCount.ToString();
        PolicyInsertMinutes = p.ControlInsertMinutes.ToString();
        PolicyLimitType = p.LimitType;
        PolicyLimitValue = p.LimitValue.ToString("0.##");
        AutoOnControlFail = p.AutoStandardizeOnControlFail;
        StandardizeOnAutoMode = p.StandardizeOnAutoMode;
        PeriodicEnabled = p.PeriodicStandardizeEnabled;
        PeriodicHours = p.PeriodicStandardizeHours.ToString();
    }

    /// <summary>保存策略配置（校验数值后覆盖写入 config/sct_policy.json）。</summary>
    [RelayCommand]
    private void SavePolicy()
    {
        PolicyError = null;
        PolicySavedMessage = null;

        if (!int.TryParse(PolicyInsertCount, out var insertCount) || insertCount < 1)
        {
            PolicyError = "插入数量必须为不小于 1 的整数";
            return;
        }

        if (!int.TryParse(PolicyInsertMinutes, out var insertMinutes) || insertMinutes < 1)
        {
            PolicyError = "时间间隔必须为不小于 1 的整数（分钟）";
            return;
        }

        if (!double.TryParse(PolicyLimitValue, out var limitValue) || limitValue <= 0)
        {
            PolicyError = "判定限值必须为大于 0 的数值";
            return;
        }

        if (!int.TryParse(PeriodicHours, out var periodicHours) || periodicHours < 1)
        {
            PolicyError = "定时标准化间隔必须为不小于 1 的整数（小时）";
            return;
        }

        if (!int.TryParse(PolicyTypePeriodDays, out var typePeriodDays) || typePeriodDays < 1)
        {
            PolicyError = "类型标准化期限必须为不小于 1 的整数（天）";
            return;
        }

        if (!int.TryParse(PolicyRecheckMax, out var recheckMax) || recheckMax < 1 || recheckMax > 5)
        {
            PolicyError = "超差验证重测次数上限必须为 1–5 的整数";
            return;
        }

        try
        {
            _policyStore.Save(new SctPolicyOptions
            {
                SettingSampleSelectionMode = PolicySettingSelectionMode,
                ControlRotationEnabled = PolicyRotationEnabled,
                ControlMatchByAlloy = PolicyMatchByAlloy,
                TypeStandardizeOnAlloySwitch = PolicyTypeOnAlloySwitch,
                TypeStandardizePeriodEnabled = PolicyTypePeriodEnabled,
                TypeStandardizePeriodDays = typePeriodDays,
                ControlRecheckMaxCount = recheckMax,
                ControlInsertMode = PolicyInsertMode,
                ControlInsertCount = insertCount,
                ControlInsertMinutes = insertMinutes,
                LimitType = PolicyLimitType,
                LimitValue = limitValue,
                AutoStandardizeOnControlFail = AutoOnControlFail,
                StandardizeOnAutoMode = StandardizeOnAutoMode,
                PeriodicStandardizeEnabled = PeriodicEnabled,
                PeriodicStandardizeHours = periodicHours,
            });
            PolicySavedMessage = "✓ 策略配置已保存";
        }
        catch (IOException ex)
        {
            PolicyError = $"保存失败：{ex.Message}";
            _errors.Collect(ErrorCodes.SctSampleMaintainFailed, "SCT 策略配置保存失败", null, ex);
        }
    }

    // ==================== 手动触发（提交策略引擎任务队列；引擎未注入时保持禁用） ====================

    /// <summary>手动触发标准化（提交 Standardize 任务，优先级高于生产分析——任务清单第 5 节⑥）。</summary>
    [RelayCommand(CanExecute = nameof(CanTriggerAnalysis))]
    private void TriggerStandardization()
    {
        TriggerError = null;
        if (_strategyEngine is null)
        {
            return;
        }

        _strategyEngine.SubmitTask(SctTaskType.Standardize, null, "人工触发");
        _ui.Invoke(() => PolicySavedMessage = "✓ 标准化任务已提交队列");
    }

    /// <summary>手动触发控制检查（提交 ControlCheck 任务，引擎按轮换/合金规则选样）。</summary>
    [RelayCommand(CanExecute = nameof(CanTriggerAnalysis))]
    private void TriggerControlCheck()
    {
        TriggerError = null;
        if (_strategyEngine is null)
        {
            return;
        }

        _strategyEngine.SubmitTask(SctTaskType.ControlCheck, null, "人工触发");
        _ui.Invoke(() => PolicySavedMessage = "✓ 控制检查任务已提交队列");
    }

    /// <summary>手动触发测高任务（提交 HeightMeasure 任务到任务队列，需选中目标标样）。</summary>
    [RelayCommand(CanExecute = nameof(CanTriggerHeight))]
    private void TriggerHeightMeasure()
    {
        TriggerError = null;
        if (_strategyEngine is null || SelectedSample is null)
        {
            return;
        }

        _strategyEngine.SubmitTask(SctTaskType.HeightMeasure, SelectedSample.SampleId, "人工测高触发");
        _ui.Invoke(() => PolicySavedMessage = $"✓ 测高任务已提交队列（{SelectedSample.SampleId}）");
    }

    /// <summary>分析类触发可用性：策略引擎已接入。</summary>
    private bool CanTriggerAnalysis() => _strategyEngine is not null;

    /// <summary>测高触发可用性：策略引擎已接入且选中目标行。</summary>
    private bool CanTriggerHeight() => _strategyEngine is not null && SelectedSample is not null;

    /// <summary>手动触发错误提示（空即无错误）。</summary>
    [ObservableProperty]
    private string? _triggerError;

    // ==================== 映射 ====================

    private static SctRowItem MapRow(SctSample s) => new()
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
        IsActive = s.IsActive,
        ActiveText = s.IsActive ? "启用" : "已禁用",
    };

    private static SctLogRowItem MapLogRow(SctOperationLog l) => new()
    {
        TimeText = l.OperationTime,
        SampleIdText = l.SampleId ?? "—",
        OperationType = l.OperationType,
        OperationTypeText = l.OperationType switch
        {
            "Standardization" => "标准化",
            "ControlCheck" => "控制检查",
            "HeightMeasure" => "测高",
            _ => l.OperationType,
        },
        TriggerReasonText = l.TriggerReason ?? "—",
        ThicknessText = l.ThicknessBefore.HasValue || l.ThicknessAfter.HasValue
            ? $"{l.ThicknessBefore?.ToString("0.0#") ?? "—"} → {l.ThicknessAfter?.ToString("0.0#") ?? "—"}"
            : "—",
        CoeffText = l.CoeffBefore is not null || l.CoeffAfter is not null
            ? $"{l.CoeffBefore ?? "—"} → {l.CoeffAfter ?? "—"}"
            : "—",
        PassText = l.IsPass.HasValue ? (l.IsPass.Value ? "✓ 合格" : "✖ 超差") : "—",
        ResultStatus = l.ResultStatus,
        ResultStatusText = l.ResultStatus switch
        {
            "Success" => "● 成功",
            "Warning" => "▲ 预警",
            "Fail" => "✖ 失败",
            _ => l.ResultStatus,
        },
    };
}
