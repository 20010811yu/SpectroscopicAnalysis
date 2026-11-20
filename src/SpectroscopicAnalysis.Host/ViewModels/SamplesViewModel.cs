using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.SctStrategy;
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

/// <summary>生产样档案行展示模型（表 Production_Sample）。</summary>
public sealed class ProductionRowItem
{
    public required string SampleId { get; init; }
    public required string SampleName { get; init; }
    public string? AlloyType { get; init; }
    public string? RackPosition { get; init; }
    public required string MethodText { get; init; }
    /// <summary>原始状态枚举串（视图 DataTrigger 切换徽章颜色）。</summary>
    public required string Status { get; init; }
    public required string StatusText { get; init; }
    /// <summary>失败原因（非空时状态列以失败色提示）。</summary>
    public string? FailureReason { get; init; }

    /// <summary>是否为分析失败回退样（视图按此切失败色徽章）。</summary>
    public bool HasFailure => FailureReason is not null;

    public required string CreatedText { get; init; }
    public required string Remark { get; init; }
}

/// <summary>分析结果行展示模型（表 Analysis_Result）。</summary>
public sealed class AnalysisResultRowItem
{
    public required string ResultIdText { get; init; }
    public required string SampleId { get; init; }
    public required string SampleName { get; init; }
    public required string MeasuredText { get; init; }
    public required string CorrectedText { get; init; }
    public required string CoeffVersionText { get; init; }
    public required string PassText { get; init; }
    public required string ResultStatusText { get; init; }
    public required string ArchiveText { get; init; }
    public required string TimeText { get; init; }
}

/// <summary>详情面板轨迹日志行展示模型（最近 10 条，时间倒序）。</summary>
public sealed class DetailLogRowItem
{
    public required string OperationTimeText { get; init; }
    public required string OperationTypeText { get; init; }
    public required string TriggerReasonText { get; init; }
    public required string ResultStatusText { get; init; }
    /// <summary>原始状态枚举串（视图 DataTrigger 切颜色）。</summary>
    public required string ResultStatus { get; init; }
}

/// <summary>
/// 样品详情面板展示模型（左表 + 右详情布局）：SCT 样与生产样共用。
/// Kind 供视图切换子表区域（Sct → 标准值；Production → 分析结果）；Status 为原始枚举串（DataTrigger 切徽章色）。
/// </summary>
public sealed class SampleDetailItem
{
    /// <summary>详情类别键："Sct" / "Production"（对应 SamplesViewModel.Table* 常量语义）。</summary>
    public required string Kind { get; init; }

    public required string SampleId { get; init; }
    public required string TypeDisplay { get; init; }
    public required string AlloyTypeText { get; init; }
    public required string RackPositionText { get; init; }
    public required string MethodText { get; init; }
    public required string ThicknessText { get; init; }
    public required string MinThicknessText { get; init; }
    public required string UsageText { get; init; }
    public required string Status { get; init; }
    public required string StatusText { get; init; }
    public required string LastUseText { get; init; }
    public required string LastHeightText { get; init; }
    public required string CreatedText { get; init; }
    public required string RemarkText { get; init; }
    /// <summary>失败原因（生产样分析失败回退待分析时展示；其余为空串即隐藏）。</summary>
    public required string FailureReasonText { get; init; }
}

/// <summary>
/// 样品档案 ViewModel：从 SQLite（SCT_Sample）加载样品并按类型筛选展示。
/// 数据库 I/O 经 Task.Run + IUiDispatcher 回 UI 线程（I/O 不阻塞 UI，开发规范第 4 节）。
/// </summary>
public sealed partial class SamplesViewModel : ObservableObject, IPageLifecycle
{
    public const string FilterAll = "All";

    // ==================== 数据表切换键（样品档案页可查看的 7 张表） ====================
    public const string TableSample = "Sample";
    public const string TableCertifiedValue = "CertifiedValue";
    public const string TableControlLimit = "ControlLimit";
    public const string TableCoefficient = "Coefficient";
    public const string TableOperationLog = "OperationLog";
    public const string TableProductionSample = "ProductionSample";
    public const string TableAnalysisResult = "AnalysisResult";

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
        new(TableProductionSample, "生产样档案（Production_Sample）"),
        new(TableAnalysisResult, "分析结果（Analysis_Result）"),
    };

    private readonly ISctSampleRepository _samples;
    private readonly IProductionSampleRepository _production;
    private readonly ISctCertifiedValueRepository _certRepo;
    private readonly IControlLimitRepository _limitRepo;
    private readonly ICorrectionCoefficientRepository _coeffRepo;
    private readonly ISctOperationLogRepository _logRepo;
    private readonly ISctStrategyEngine _strategyEngine;
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
    private List<ProductionRowItem> _allProduction = new();
    private List<AnalysisResultRowItem> _allAnalysisResults = new();

    [ObservableProperty]
    private ObservableCollection<SampleRowItem> _sctSamples = new();

    [ObservableProperty]
    private ObservableCollection<ProductionRowItem> _productionRows = new();

    [ObservableProperty]
    private ObservableCollection<AnalysisResultRowItem> _analysisResultRows = new();

    /// <summary>当前展示的数据表（默认样品台账；仅样品表显示录入/筛选控件）。
    /// 变化即触发该表数据加载（ComboBox 双向绑定直接改此属性，也走同一入口）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSampleTable))]
    [NotifyPropertyChangedFor(nameof(ActiveTableDisplay))]
    [NotifyPropertyChangedFor(nameof(SearchText))]
    [NotifyPropertyChangedFor(nameof(SearchHint))]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    private string _activeTable = TableSample;

    /// <summary>当前激活表的进行中加载任务（切表后可 await 确定性等待；初始为已完成）。</summary>
    private Task _activeTableLoad = Task.CompletedTask;

    /// <summary>搜索框水印提示词（随激活数据表切换，描述该表的可搜索字段）。</summary>
    public string SearchHint => ActiveTable switch
    {
        TableCertifiedValue => "搜索标准值（编号 / 元素）",
        TableControlLimit => "搜索控制限（编号 / 元素）",
        TableCoefficient => "搜索校正系数（方法 / 元素 / 来源样品）",
        TableOperationLog => "搜索操作日志（编号 / 操作类型 / 触发原因）",
        TableProductionSample => "搜索生产样（编号 / 名称 / 合金 / 状态）",
        TableAnalysisResult => "搜索分析结果（编号 / 归档位置 / 状态）",
        _ => "搜索关键字（编号 / 名称）",
    };

    /// <summary>当前表是否已输入关键词（控制搜索框水印提示词的显隐）。</summary>
    public bool HasSearchText => !string.IsNullOrEmpty(GetTableSearchText(ActiveTable));

    partial void OnActiveTableChanged(string value)
    {
        SelectedRow = null;                     // 切表清空选中与详情面板
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
            case TableProductionSample:
                await LoadProductionSamplesAsync();
                break;
            case TableAnalysisResult:
                await LoadAnalysisResultsAsync();
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

    /// <summary>各数据表独立缓存的关键词（键 = ActiveTable 表键；切表互不影响）。</summary>
    private readonly Dictionary<string, string> _tableSearchTexts = new();

    /// <summary>查询关键词（模糊匹配当前表关键字段；空即不过滤）。
    /// 按激活表路由存取：切表时搜索框自动切换为该表上次输入的关键词。</summary>
    public string SearchText
    {
        get => GetTableSearchText(ActiveTable);
        set
        {
            if (string.Equals(GetTableSearchText(ActiveTable), value, StringComparison.Ordinal))
            {
                return;
            }

            _tableSearchTexts[ActiveTable] = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSearchText));
        }
    }

    /// <summary>生产样档案表搜索关键词（兼容存取入口，内部同样走按表缓存）。</summary>
    public string ProductionSearchText
    {
        get => GetTableSearchText(TableProductionSample);
        set
        {
            if (string.Equals(GetTableSearchText(TableProductionSample), value, StringComparison.Ordinal))
            {
                return;
            }

            _tableSearchTexts[TableProductionSample] = value;
            OnPropertyChanged();
        }
    }

    /// <summary>分析结果表搜索关键词（兼容存取入口，内部同样走按表缓存）。</summary>
    public string AnalysisSearchText
    {
        get => GetTableSearchText(TableAnalysisResult);
        set
        {
            if (string.Equals(GetTableSearchText(TableAnalysisResult), value, StringComparison.Ordinal))
            {
                return;
            }

            _tableSearchTexts[TableAnalysisResult] = value;
            OnPropertyChanged();
        }
    }

    /// <summary>读取指定表缓存的关键词（无记录返回空串）。</summary>
    private string GetTableSearchText(string tableKey) =>
        _tableSearchTexts.TryGetValue(tableKey, out var text) ? text : string.Empty;

    /// <summary>分页大小（各数据表每页 15 行，统一行高完整展示）。</summary>
    public const int PageSize = 15;

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

    // ==================== 样品详情面板（左表 + 右详情布局；样品台账/生产样档案两表启用） ====================

    /// <summary>左侧表格当前选中行（DataGrid SelectedItem 绑定；SCT 样与生产样两表共用）。</summary>
    [ObservableProperty]
    private object? _selectedRow;

    /// <summary>右侧详情面板内容（null 即不展示；选中行变化/切表/分页时重算）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailVisible))]
    private SampleDetailItem? _detail;

    /// <summary>详情面板标准值子表（仅 SCT 样；经 ISctCertifiedValueRepository.GetBySampleId）。</summary>
    [ObservableProperty]
    private ObservableCollection<CertifiedValueRowItem> _detailCertifiedValues = new();

    /// <summary>详情面板分析结果子表（仅生产样；经 IProductionSampleRepository.GetResults）。</summary>
    [ObservableProperty]
    private ObservableCollection<AnalysisResultRowItem> _detailResults = new();

    /// <summary>详情面板轨迹日志子表（最近 10 条，时间倒序；经 ISctOperationLogRepository.GetRecentBySampleId）。</summary>
    [ObservableProperty]
    private ObservableCollection<DetailLogRowItem> _detailLogs = new();

    /// <summary>是否显示右侧详情面板（仅两张目标表且选中行非空）。</summary>
    public bool DetailVisible =>
        Detail is not null && ActiveTable is TableSample or TableProductionSample;

    /// <summary>选中行变化 → 重算详情面板（含子表后台查询；失败走错误链不弹窗）。</summary>
    partial void OnSelectedRowChanged(object? value) => _ = RefreshDetailAsync(value);

    /// <summary>按选中行装配详情：字段直接取行展示模型，标准值/结果/日志后台查询后回填。</summary>
    private async Task RefreshDetailAsync(object? row)
    {
        switch (row)
        {
            case SampleRowItem s:
                Detail = MapSctDetail(s);
                break;
            case ProductionRowItem p:
                Detail = MapProductionDetail(p);
                break;
            default:
                ClearDetail();
                return;
        }

        var sampleId = row switch
        {
            SampleRowItem s2 => s2.SampleId,
            ProductionRowItem p2 => p2.SampleId,
            _ => string.Empty,
        };
        var kind = ((SampleDetailItem)Detail!).Kind;
        var detailSampleName = row switch
        {
            ProductionRowItem p3 => p3.SampleName,
            _ => string.Empty,
        };

        try
        {
            // 子表数据后台查询（I/O 不阻塞 UI）；结果回 UI 线程整体替换
            var certs = kind == "Sct"
                ? await Task.Run(() => _certRepo.GetBySampleId(sampleId).Select(c => new CertifiedValueRowItem
                {
                    Id = c.Id,
                    SampleId = c.SampleId,
                    Element = c.Element,
                    ValueText = c.CertifiedValue.ToString("0.####"),
                    Unit = c.Unit,
                    UncertaintyText = c.Uncertainty?.ToString("0.####") ?? "—",
                }).ToList())
                : new List<CertifiedValueRowItem>();
            var results = kind == "Production"
                ? await Task.Run(() => _production.GetResults(sampleId).Select(r => MapAnalysisRow(r, detailSampleName)).ToList())
                : new List<AnalysisResultRowItem>();
            var logs = kind == "Sct"
                ? await Task.Run(() => _logRepo.GetRecentBySampleId(sampleId, 10).Select(l => new DetailLogRowItem
            {
                OperationTimeText = l.OperationTime,   // 完整时间（yyyy-MM-dd HH:mm:ss）
                OperationTypeText = l.OperationType switch
                {
                    "Standardization" => "标准化",
                    "ControlCheck" => "控制检查",
                    "HeightMeasure" => "测高",
                    _ => l.OperationType,
                },
                TriggerReasonText = l.TriggerReason ?? "—",
                ResultStatusText = l.ResultStatus switch
                {
                    "Success" => "● 成功",
                    "Warning" => "▲ 预警",
                    "Fail" => "✖ 失败",
                    _ => l.ResultStatus,
                },
                ResultStatus = l.ResultStatus,
            }).ToList())
                : new List<DetailLogRowItem>();

            _ui.Invoke(() =>
            {
                // 期间选中行已变化则丢弃本轮结果（防旧查询覆盖新详情）
                if (!ReferenceEquals(SelectedRow, row))
                {
                    return;
                }

                DetailCertifiedValues = new ObservableCollection<CertifiedValueRowItem>(certs);
                DetailResults = new ObservableCollection<AnalysisResultRowItem>(results);
                DetailLogs = new ObservableCollection<DetailLogRowItem>(logs);
            });
        }
        catch (Exception ex)
        {
            _errors.Collect(ErrorCodes.UnhandledException, "样品详情子表查询失败", sampleId, ex);
        }
    }

    /// <summary>清空详情面板与全部子表。</summary>
    private void ClearDetail()
    {
        Detail = null;
        DetailCertifiedValues = new ObservableCollection<CertifiedValueRowItem>();
        DetailResults = new ObservableCollection<AnalysisResultRowItem>();
        DetailLogs = new ObservableCollection<DetailLogRowItem>();
    }

    private static SampleDetailItem MapSctDetail(SampleRowItem s) => new()
    {
        Kind = "Sct",
        SampleId = s.SampleId,
        TypeDisplay = s.TypeDisplay,
        AlloyTypeText = s.AlloyType ?? "—",
        RackPositionText = s.RackPosition ?? "—",
        MethodText = string.IsNullOrEmpty(s.AssociatedMethodText) ? "—" : s.AssociatedMethodText,
        ThicknessText = s.ThicknessText,
        MinThicknessText = s.MinThicknessText,
        UsageText = s.UsageText,
        Status = s.Status,
        StatusText = s.StatusText,
        LastUseText = s.LastUseText,
        LastHeightText = s.LastHeightText,
        CreatedText = "—",
        RemarkText = "—",
        FailureReasonText = string.Empty,
    };

    private static SampleDetailItem MapProductionDetail(ProductionRowItem p) => new()
    {
        Kind = "Production",
        SampleId = p.SampleId,
        TypeDisplay = "生产样",
        AlloyTypeText = p.AlloyType ?? "—",
        RackPositionText = p.RackPosition ?? "—",
        MethodText = p.MethodText,
        ThicknessText = "—",
        MinThicknessText = "—",
        UsageText = "—",
        Status = p.Status,
        StatusText = p.StatusText,
        LastUseText = "—",
        LastHeightText = "—",
        CreatedText = p.CreatedText,
        RemarkText = p.Remark,
        FailureReasonText = p.FailureReason ?? string.Empty,
    };

    /// <summary>分页/筛选/删除导致行集合整体替换后，按 SampleID 重选保持详情面板；
    /// 无选中时默认选中第一行（详情面板默认展示，样品台账/生产样档案两表生效）。</summary>
    private void RestoreSelection()
    {
        // 其余 5 张表无详情面板，不做默认选中
        if (ActiveTable is not (TableSample or TableProductionSample))
        {
            return;
        }

        var current = SelectedRow;
        var sampleId = current switch
        {
            SampleRowItem s => s.SampleId,
            ProductionRowItem p => p.SampleId,
            _ => null,
        };

        object? reselected = ActiveTable switch
        {
            TableProductionSample => (object?)ProductionRows.FirstOrDefault(r => r.SampleId == sampleId),
            _ => (object?)SctSamples.FirstOrDefault(r => r.SampleId == sampleId),
        };
        // 旧选中还在当前页则保持；否则回退到第一行（空表即 null → 清空详情）
        SelectedRow = reselected ?? ActiveTable switch
        {
            TableProductionSample => (object?)ProductionRows.FirstOrDefault(),
            _ => (object?)SctSamples.FirstOrDefault(),
        };
    }

    // ==================== 样品注册（任务清单"入库初始化"） ====================

    /// <summary>样品类型下拉选项（类型键：生产样默认在前；控样/检查样/类型标准化样）。
    /// 必须为实例属性供 XAML ItemsSource 绑定（静态字段不可绑定）。</summary>
    public IReadOnlyList<string> SampleTypeOptions { get; } = new[]
    {
        SampleType.Production, SampleType.SettingUp, SampleType.Control, SampleType.TypeStd,
    };

    /// <summary>分析方法下拉选项（生产样注册用；与种子数据方法清单一致）。</summary>
    public IReadOnlyList<string> AnalysisMethodOptions { get; } = new[]
    {
        "GB/T 7999-A", "GB/T 7999-B", "ASTM E1251-A", "ASTM E1251-B",
    };

    [ObservableProperty]
    private bool _isRegisterOpen;

    [ObservableProperty]
    private string _regSampleId = string.Empty;

    [ObservableProperty]
    private string _regSampleName = string.Empty;

    [ObservableProperty]
    private string _regSampleType = SampleType.Production;

    /// <summary>分析方法（生产样注册必选；其余类型不显示）。</summary>
    [ObservableProperty]
    private string? _regAnalysisMethod;

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

    /// <summary>切换注册表单显隐；首次展开时切回样品台账表（便于直接注册 SCT 样），切表不收起面板。</summary>
    [RelayCommand]
    private void ToggleRegister()
    {
        IsRegisterOpen = !IsRegisterOpen;
        if (IsRegisterOpen && ActiveTable != TableSample)
        {
            ActiveTable = TableSample;
        }
    }

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

        var type = TypeDisplayToKey(RegSampleType);
        var sample = type == SampleType.Production ? null : BuildSampleFromForm();
        var productionSample = type == SampleType.Production ? BuildProductionSampleFromForm() : null;
        IsRegistering = true;
        RegisterError = null;
        try
        {
            // 重复校验（按类型查各自表：生产样查 Production_Sample，其余查 SCT_Sample 编号/架位）
            var dupError = await Task.Run(() =>
            {
                var errors = new List<string>();
                if (productionSample is not null)
                {
                    if (_production.GetAll().FirstOrDefault(p =>
                            string.Equals(p.SampleId, productionSample.SampleId, StringComparison.Ordinal)) is not null)
                    {
                        errors.Add($"样品编号已存在：{productionSample.SampleId}");
                    }
                }
                else if (sample is not null)
                {
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
                }
                return errors.Count > 0 ? string.Join("\n", errors) : null;
            });
            if (dupError is not null)
            {
                RegisterError = dupError;
                _errors.Collect(ErrorCodes.SampleRegisterRejected, dupError, RegSampleId.Trim());
                return;
            }

            try
            {
                if (productionSample is not null)
                {
                    await Task.Run(() => _production.Insert(productionSample));
                }
                else
                {
                    await Task.Run(() => _samples.Insert(sample!));
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                RegisterError = $"入库失败：{ex.Message}";
                _errors.Collect(ErrorCodes.SampleInsertFailed, "样品入库写库失败", RegSampleId.Trim(), ex);
                return;
            }
            // 注册完成自动加入任务队列：四类样品均入分析队列，任务类型按样品类型映射（引擎同目标同类型自动去重）
            var taskType = productionSample is not null
                ? SctTaskType.Production
                : type switch
                {
                    SampleType.SettingUp => SctTaskType.Standardize,
                    SampleType.Control => SctTaskType.ControlCheck,
                    SampleType.TypeStd => SctTaskType.TypeStandardize,
                    _ => SctTaskType.Production,
                };
            var queuedId = productionSample is not null ? productionSample.SampleId : sample!.SampleId;
            _strategyEngine.SubmitTask(taskType, queuedId, "样品注册自动入队");

            _ui.Invoke(() =>
            {
                ClearRegisterForm();
                IsRegisterOpen = false;
            });
            // 入库后刷新当前激活表（生产样刷新生产样表，其余刷新台账表）
            await LoadCommand.ExecuteAsync(null);
        }
        finally
        {
            _ui.Invoke(() => IsRegistering = false);
        }
    }

    /// <summary>表单校验（按注册类型条件化）；返回错误消息，null 表示通过。</summary>
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

        var type = TypeDisplayToKey(RegSampleType);
        if (type == SampleType.Production)
        {
            // 生产样：编号/名称 + 分析方法
            if (string.IsNullOrWhiteSpace(RegAnalysisMethod))
            {
                return "请选择分析方法";
            }
            return null;
        }

        // 控样/检查样/类型标准样：架位 + 厚度 + 使用次数
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
        // 合金约束：仅类型标准化样必填合金（数据库触发器兜底，此处前置校验）
        if (type == SampleType.TypeStd && string.IsNullOrWhiteSpace(RegAlloyType))
        {
            return "类型标准化样必须填写关联合金";
        }
        if (type != SampleType.TypeStd && !string.IsNullOrWhiteSpace(RegAlloyType))
        {
            return "仅类型标准化样可关联合金，控样/检查样请留空合金";
        }
        return null;
    }

    /// <summary>生产样注册：按表单构造 ProductionSample（写 Production_Sample，无厚度/使用次数字段）。</summary>
    private ProductionSample BuildProductionSampleFromForm() => new()
    {
        SampleId = RegSampleId.Trim(),
        SampleName = RegSampleName.Trim(),
        SampleType = SampleType.Production,
        AssociatedMethod = RegAnalysisMethod!.Trim(), // 校验已确保非空
        Status = ProductionStatus.Pending,
        CreatedTime = DateTime.Now,
    };

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
        RegAnalysisMethod = null;
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
        "控样" => SampleType.SettingUp,
        "检查样" => SampleType.Control,
        "类型标准化样" => SampleType.TypeStd,
        "生产样品" => SampleType.Production,
        _ => display,
    };

    public SamplesViewModel(
        ISctSampleRepository samples,
        IProductionSampleRepository production,
        ISctCertifiedValueRepository certifiedValues,
        IControlLimitRepository controlLimits,
        ICorrectionCoefficientRepository coefficients,
        ISctOperationLogRepository operationLogs,
        ISctStrategyEngine strategyEngine,
        IUiDispatcher ui,
        IDialogService dialogs,
        IErrorCollector errors)
    {
        _samples = samples;
        _production = production;
        _certRepo = certifiedValues;
        _limitRepo = controlLimits;
        _coeffRepo = coefficients;
        _logRepo = operationLogs;
        _strategyEngine = strategyEngine;
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

    /// <summary>加载生产样档案表（Production_Sample；全量入缓存后统一分页/专属搜索）。</summary>
    private async Task LoadProductionSamplesAsync()
    {
        var rows = await Task.Run(() => _production.GetAll().Select(MapProductionRow).ToList());
        _ui.Invoke(() =>
        {
            _allProduction = rows;
            RefreshView();
        });
    }

    /// <summary>加载分析结果表（Analysis_Result；全量入缓存后统一分页/专属搜索）。</summary>
    private async Task LoadAnalysisResultsAsync()
    {
        var rows = await Task.Run(() =>
        {
            // 名称按 SampleID 关联生产样档案（一次后台查询内完成，避免逐行查库）
            var names = _production.GetAll().ToDictionary(p => p.SampleId, p => p.SampleName);
            return _production.GetAllResults()
                .Select(r => MapAnalysisRow(r, names.GetValueOrDefault(r.SampleId, "—")))
                .ToList();
        });
        _ui.Invoke(() =>
        {
            _allAnalysisResults = rows;
            RefreshView();
        });
    }

    private static ProductionRowItem MapProductionRow(ProductionSample p) => new()
    {
        SampleId = p.SampleId,
        SampleName = p.SampleName,
        AlloyType = p.AlloyType,
        RackPosition = p.RackPosition,
        MethodText = p.AssociatedMethod ?? "—",
        Status = p.Status,
        StatusText = p.Status == ProductionStatus.Completed
            ? "✓ 已完成"
            : p.FailureReason is null ? "● 待分析" : "✖ 分析失败",
        FailureReason = p.FailureReason,
        CreatedText = p.CreatedTime?.ToString("MM-dd HH:mm") ?? "—",
        Remark = p.Remark ?? "—",
    };

    /// <summary>分析结果行映射（sampleName 由调用方按表上下文解析：整表查生产样档案，详情传当前行名称）。</summary>
    private static AnalysisResultRowItem MapAnalysisRow(AnalysisResult r, string sampleName) => new()
    {
        ResultIdText = $"#{r.ResultId}",
        SampleId = r.SampleId,
        SampleName = sampleName,
        MeasuredText = r.MeasuredConc?.ToString("0.####") ?? "—",
        CorrectedText = r.CorrectedConc?.ToString("0.####") ?? "—",
        CoeffVersionText = r.CoeffVersion is { } v ? $"v{v}" : "—",
        PassText = r.IsPass ? "✓ 合格" : "✖ 不合格",
        ResultStatusText = r.ResultStatus switch
        {
            AnalysisResultStatus.Success => "● 成功",
            AnalysisResultStatus.Warning => "▲ 预警",
            AnalysisResultStatus.Fail => "✖ 失败",
            _ => r.ResultStatus,
        },
        ArchiveText = r.ArchivePosition ?? "—（未归档）",
        TimeText = r.OperationTime?.ToString("MM-dd HH:mm") ?? "—",
    };

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
        if (key == FilterAll)
        {
            // 点「全部」= 查看所有数据：清空当前表搜索框（setter 已按表路由触发刷新，HasSearchText 联动水印）
            SearchText = string.Empty;
        }

        // 切换类型筛选回到第 1 页
        RefreshView(resetPage: true);
    }

    private void ApplyFilter(string key)
    {
        FilterKey = key;
        RefreshView();
    }

    /// <summary>搜索命令：关键词变化后回到第 1 页刷新（回车/按钮触发，对当前激活表生效）。
    /// 无命中时保留当前数据页并弹窗提示（不再清空表格）。</summary>
    [RelayCommand]
    private void Search()
    {
        RefreshView(resetPage: true, keepPageWhenEmpty: true);
        if (_lastSearchKeptPage)
        {
            var keyword = SearchText?.Trim();
            _dialogs.Alert("未找到数据", $"未找到与「{keyword}」匹配的数据，已保留当前数据页。");
        }
    }

    /// <summary>筛选 + 关键词过滤 + 分页切片统一入口（按当前激活表分发到对应集合）。</summary>
    private void RefreshView(bool resetPage = false, bool keepPageWhenEmpty = false)
    {
        // 关键词按激活表路由（各表缓存独立，切表互不影响）
        var keyword = SearchText?.Trim();
        _lastSearchKeptPage = false;
        switch (ActiveTable)
        {
            case TableCertifiedValue:
                ApplyPagedView(_allCertified, r => Matches(r.SampleId, keyword) || Matches(r.Element, keyword), resetPage, keepPageWhenEmpty,
                    page => CertifiedValues = new ObservableCollection<CertifiedValueRowItem>(page));
                break;
            case TableControlLimit:
                ApplyPagedView(_allLimits, r => Matches(r.SampleId, keyword) || Matches(r.Element, keyword), resetPage, keepPageWhenEmpty,
                    page => ControlLimitRows = new ObservableCollection<ControlLimitRowItem>(page));
                break;
            case TableCoefficient:
                ApplyPagedView(_allCoeffs, r => Matches(r.MethodId, keyword) || Matches(r.Element, keyword) || Matches(r.SourceSampleText, keyword), resetPage, keepPageWhenEmpty,
                    page => CoefficientRows = new ObservableCollection<CoefficientRowItem>(page));
                break;
            case TableOperationLog:
                ApplyPagedView(_allLogs, r => Matches(r.SampleIdText, keyword) || Matches(r.OperationTypeText, keyword) || Matches(r.TriggerReasonText, keyword), resetPage, keepPageWhenEmpty,
                    page => OperationLogRows = new ObservableCollection<OperationLogRowItem>(page));
                break;
            case TableProductionSample:
                // 生产样档案表：关键词按表路由（SearchText 即当前表缓存的关键词）
                ApplyPagedView(_allProduction, r => Matches(r.SampleId, keyword) || Matches(r.SampleName, keyword)
                    || Matches(r.AlloyType, keyword) || Matches(r.Status, keyword), resetPage, keepPageWhenEmpty,
                    page => ProductionRows = new ObservableCollection<ProductionRowItem>(page));
                break;
            case TableAnalysisResult:
                // 分析结果表：关键词按表路由
                ApplyPagedView(_allAnalysisResults, r => Matches(r.SampleId, keyword) || Matches(r.ArchiveText, keyword)
                    || Matches(r.ResultStatusText, keyword), resetPage, keepPageWhenEmpty,
                    page => AnalysisResultRows = new ObservableCollection<AnalysisResultRowItem>(page));
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

                ApplyPagedView(filtered.ToList(), r => Matches(r.SampleId, keyword) || Matches(r.SampleName, keyword), resetPage, keepPageWhenEmpty,
                    page => SctSamples = new ObservableCollection<SampleRowItem>(page));
                break;
        }

        RestoreSelection();   // 行集合整体替换后按 SampleID 重选，保持详情面板
    }

    /// <summary>上次刷新是否因搜索无命中而保留了原数据页（Search 据此弹窗提示）。</summary>
    private bool _lastSearchKeptPage;

    /// <summary>通用分页切片：统计总量/页数 → 收敛页码 → 输出当前页行。
    /// keepPageWhenEmpty（搜索路径）：过滤后为空且关键词非空时保留原数据页不动，仅置标记供弹窗提示。</summary>
    private void ApplyPagedView<T>(IReadOnlyList<T> source, Func<T, bool>? predicate, bool resetPage, bool keepPageWhenEmpty, Action<List<T>> sink)
    {
        IEnumerable<T> query = source;
        if (predicate is not null)
        {
            query = query.Where(predicate);
        }

        var list = query.ToList();
        var keyword = SearchText?.Trim();
        if (keepPageWhenEmpty && list.Count == 0 && !string.IsNullOrEmpty(keyword))
        {
            _lastSearchKeptPage = true;     // 无命中：保留当前数据页，不更新任何分页状态
            return;
        }

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

    /// <summary>分页脚页码输入框内容（跳页用；解析失败忽略不清空，便于修正）。</summary>
    [ObservableProperty]
    private string _pageJumpInput = string.Empty;

    /// <summary>页脚「跳转」：解析输入页码直达（经 GoToPage 收敛越界），非法输入忽略。</summary>
    [RelayCommand]
    private void JumpToPage()
    {
        if (!int.TryParse(PageJumpInput?.Trim(), out var page))
        {
            return;
        }

        GoToPage(page);
        PageJumpInput = string.Empty;
    }

    // ==================== 样品删除（弹窗确认，危险操作） ====================

    /// <summary>删除：先弹窗确认（不动行数据），确认后按行类型事务级联删除——
    /// SCT 样删 样品+标准值+控制限（操作日志保留）；生产样删 档案+全部分析结果。
    /// 参数取 object：SelectedRow 为样品台账/生产样档案两表共享，两类型都会走到按钮 CanExecute，
    /// 强类型 RelayCommand&lt;T&gt; 对类型不符的参数会抛 ArgumentException（进程崩溃），故此处宽容判型。</summary>
    [RelayCommand]
    private async Task DeleteAsync(object? parameter)
    {
        if (parameter is not (SampleRowItem or ProductionRowItem) || IsLoading)
        {
            return;
        }

        DeleteError = null;
        if (parameter is SampleRowItem row)
        {
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
            return;
        }

        // 生产样：事务级联删除 档案 + 全部分析结果
        var production = (ProductionRowItem)parameter;
        if (!_dialogs.Confirm("删除生产样品", $"确认删除生产样品「{production.SampleId} {production.SampleName}」？\n将级联删除其全部分析结果。"))
        {
            return;
        }

        try
        {
            await Task.Run(() => _production.DeleteWithResults(production.SampleId));
            _ui.Invoke(() =>
            {
                _allProduction = _allProduction.Where(r => !ReferenceEquals(r, production)).ToList();
                _allAnalysisResults = _allAnalysisResults
                    .Where(r => !string.Equals(r.SampleId, production.SampleId, StringComparison.Ordinal))
                    .ToList();
                RefreshView();
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            DeleteError = $"删除失败：{ex.Message}";
            _errors.Collect(ErrorCodes.SampleDeleteFailed, "生产样品删除失败", production.SampleId, ex);
        }
    }

    private static SampleRowItem MapRow(SctSample s) => new()
    {
        SampleId = s.SampleId,
        SampleName = s.SampleName,
        SampleTypeKey = s.SampleType,
        TypeDisplay = s.SampleType switch
        {
            SampleType.SettingUp => "控样",
            SampleType.Control => "检查样",
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
