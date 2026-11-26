using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.Ui;

using SpectroscopicAnalysis.Host.SctStrategy;
using SpectroscopicAnalysis.Host.Workflow;

namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>任务队列行展示模型（生产监控「任务队列」模块，来源策略引擎队列快照）。</summary>
public sealed class QueueTaskItem
{
    /// <summary>任务编号（原始 int，置顶命令参数用）。</summary>
    public required int TaskId { get; init; }
    public required string TaskIdText { get; init; }
    public required string TaskTypeText { get; init; }
    public required string SampleIdText { get; init; }
    /// <summary>样品名称（SCT/生产样档案查询；无档案时显示 —）。</summary>
    public required string SampleNameText { get; init; }
    /// <summary>合金类型（SCT/生产样档案查询；无档案或未填时显示 —）。</summary>
    public required string AlloyTypeText { get; init; }
    public required string ReasonText { get; init; }
    public required string EnqueueTimeText { get; init; }
    /// <summary>是否执行中（视图切绿色徽章）。</summary>
    public bool IsRunning { get; init; }
    /// <summary>是否可手动置顶（排队中的生产任务；SCT 任务保持 P29 自动调度，执行中不可调整）。</summary>
    public bool CanMoveTop { get; init; }
    /// <summary>是否可删除（排队中任务均可；执行中/已终结不可；删除仅移出队列，不删样品档案与数据）。</summary>
    public bool CanRemove { get; init; }
    /// <summary>任务状态文本（排队中/执行中/已完成/失败）。</summary>
    public required string StatusText { get; init; }
}

/// <summary>
/// 生产监控 ViewModel：当前任务卡（占位）+ KPI 四卡 + 任务队列 + 样品注册。
/// 数据源为策略引擎队列快照与生产样/SCT 档案；I/O 在后台线程，结果经 IUiDispatcher 回 UI 线程（开发规范第 4 节）。
/// </summary>
public sealed partial class ProductionMonitorViewModel : ObservableObject, IPageLifecycle
{
    /// <summary>任务队列每页行数（超过后自动分页）。</summary>
    public const int QueuePageSize = 10;

    /// <summary>KPI 定时刷新周期（秒；倒计时/今日已完成等随时间自然变化的数据靠它保鲜）。</summary>
    private static readonly TimeSpan KpiRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly ISctOperationLogRepository _logs;
    private readonly ISctSampleRepository _samples;
    private readonly IProductionSampleRepository _production;
    private readonly ISctStrategyEngine _engine;
    private readonly IWorkflowOrchestrator _orchestrator;
    private readonly IUiDispatcher _ui;
    private readonly IErrorCollector _errors;
    private readonly Ui.IDialogService _dialogs;
    private readonly SampleManager.ISampleRegistrationService _registration;
    private readonly SampleManager.ISampleDataChangeNotifier _sampleDataChanged;

    // ==================== KPI 四卡 ====================

    /// <summary>「今日已完成」数（Status=Completed 的生产样；有结果的按最新结果时间=今日过滤，无结果也计入）。</summary>
    [ObservableProperty]
    private int _todayCompletedCount;

    /// <summary>「队列待分析」数（引擎当前排队任务数，不含执行中与历史）。</summary>
    [ObservableProperty]
    private int _queuePendingCount;

    /// <summary>「检查样插入倒计时」文本（ByCount：n / 阈值；ByTime：已过分钟 / 间隔 分钟）。</summary>
    [ObservableProperty]
    private string _controlCountdownText = "—";

    /// <summary>检查样插入 KPI 标题（随插入模式切换：按数量=计数，按时间=倒计时）。</summary>
    [ObservableProperty]
    private string _controlCountdownTitle = "检查样插入倒计时";

    /// <summary>KPI 定时刷新（导航后启动一次；页面为单例常驻，随宿主退出结束）。</summary>
    private PeriodicTimer? _kpiTimer;

    /// <summary>倒计时逐秒节拍（导航后启动一次；轻量：仅按缓存的到期时刻重算文本，不做任何 I/O）。</summary>
    private PeriodicTimer? _countdownTimer;

    /// <summary>倒计时缓存快照：ByTime 到期时刻（null = 无记录已到期 / ByCount 模式）。</summary>
    private DateTime? _controlDueTime;

    /// <summary>倒计时缓存快照：当前插入模式（ControlInsertMode 常量键）。</summary>
    private string _countdownMode = ControlInsertMode.ByCount;

    /// <summary>倒计时缓存快照：按数量计数值与阈值（ByCount 文本逐秒重算时复用）。</summary>
    private int _productionSinceControl;
    private int _controlInsertCount;

    /// <summary>「定期标准化倒计时」文本（未启用/到期待执行/剩余时间）。</summary>
    [ObservableProperty]
    private string _standardizeCountdownText = "—";

    /// <summary>「类型标准化期限倒计时」文本（未启用/到期待执行/剩余时间）。</summary>
    [ObservableProperty]
    private string _typeStdCountdownText = "—";

    /// <summary>标准化期限缓存快照：开关与到期时刻（null = 未启用或无记录已到期）。</summary>
    private bool _stdEnabled;
    private DateTime? _stdDueTime;
    private bool _typeStdEnabled;
    private DateTime? _typeStdDueTime;

    /// <summary>剩余时间文本：未启用→"未启用"；到期/无记录→"到期待执行"；未到期→"剩余 hh:mm:ss"（超 24h 带"X天"）。</summary>
    private static string FormatCountdown(bool enabled, DateTime? dueTime)
    {
        if (!enabled)
        {
            return "未启用";
        }

        if (dueTime is not { } due || due <= DateTime.Now)
        {
            return "到期待执行";
        }

        var remain = due - DateTime.Now;
        return remain.TotalDays >= 1
            ? $"剩余 {remain.Days}天 {remain.Hours:00}:{remain.Minutes:00}:{remain.Seconds:00}"
            : $"剩余 {remain:hh\\:mm\\:ss}";
    }

    /// <summary>启动 KPI 定时刷新循环（重复导航防重启；取消/异常静默收尾——宿主关闭路径）。</summary>
    private void StartKpiTimer()
    {
        if (_kpiTimer is not null)
        {
            return;
        }

        _kpiTimer = new PeriodicTimer(KpiRefreshInterval);
        _ = RunKpiTimerAsync(_kpiTimer);
        // 倒计时逐秒节拍：仅重算显示文本（秒级精度），数据快照仍由 30 秒 KPI 刷新维护
        _countdownTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _ = RunCountdownTimerAsync(_countdownTimer);
    }

    /// <summary>倒计时逐秒循环体：每秒在 UI 线程重算剩余时间文本。</summary>
    private async Task RunCountdownTimerAsync(PeriodicTimer timer)
    {
        try
        {
            while (await timer.WaitForNextTickAsync())
            {
                UpdateCountdownText();
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主关闭取消定时器，属正常退出路径
        }
    }

    /// <summary>按缓存快照重算倒计时文本（须在 UI 线程；纯内存计算，秒级精度）。</summary>
    private void UpdateCountdownText()
    {
        // 标准化期限倒计时（两种插入模式下都需逐秒刷新，先于检查样分支重算）
        StandardizeCountdownText = FormatCountdown(_stdEnabled, _stdDueTime);
        TypeStdCountdownText = FormatCountdown(_typeStdEnabled, _typeStdDueTime);

        if (_countdownMode != ControlInsertMode.ByTime)
        {
            ControlCountdownText = $"{_productionSinceControl} / {_controlInsertCount}";
            return;
        }

        // ByTime：剩余 = 到期时刻 - 现在（hh:mm:ss，秒级精度）；无到期时刻（无上次记录）或已到期 → 到期待插入
        ControlCountdownText = _controlDueTime is { } due && due > DateTime.Now
            ? $"剩余 {due - DateTime.Now:hh\\:mm\\:ss}"
            : "到期待插入";
    }

    /// <summary>KPI 定时刷新循环体。</summary>
    private async Task RunKpiTimerAsync(PeriodicTimer timer)
    {
        try
        {
            while (await timer.WaitForNextTickAsync())
            {
                RefreshKpis();
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主关闭取消定时器，属正常退出路径
        }
    }

    /// <summary>
    /// 刷新 KPI 三卡：今日已完成（Status=Completed 生产样，无结果也计入）/ 队列待分析 / 检查样倒计时。
    /// I/O 在后台线程，结果经 IUiDispatcher 回 UI 线程（开发规范第 4 节）。
    /// </summary>
    private void RefreshKpis()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // 今日已完成：Production_Sample 中 Status=Completed 的样品；
                // 有分析结果的按最新结果时间=今日过滤，无结果记录的完成样品也计入（表无完成时间字段）
                var today = DateTime.Today;
                var latestResultTime = _production.GetAllResults()
                    .GroupBy(r => r.SampleId)
                    .ToDictionary(g => g.Key, g => g.Max(r => r.OperationTime));
                var completed = _production.GetAll()
                    .Count(p => p.Status == ProductionStatus.Completed &&
                        (!latestResultTime.TryGetValue(p.SampleId, out var t) || t?.Date == today));
                var countdown = _engine.GetControlCountdown();
                var stdCountdown = _engine.GetStandardizeCountdown();

                _ui.Invoke(() =>
                {
                    TodayCompletedCount = completed;
                    QueuePendingCount = _engine.GetQueuedTasks().Count;
                    // 标题随插入模式切换：按数量=计数语义，按时间=倒计时语义（此前标题写死「倒计时」，ByCount 下语义不符）
                    ControlCountdownTitle = countdown.Mode == ControlInsertMode.ByTime
                        ? "检查样插入倒计时"
                        : "检查样插入计数";
                    // 缓存倒计时快照：到期时刻/模式/计数值供 1 秒轻量节拍逐秒重算（重型查询仍 30 秒一轮）
                    _countdownMode = countdown.Mode;
                    _controlDueTime = countdown.ControlDueTime;
                    _productionSinceControl = countdown.ProductionSinceControl;
                    _controlInsertCount = countdown.ControlInsertCount;
                    _stdEnabled = stdCountdown.PeriodicEnabled;
                    _stdDueTime = stdCountdown.StandardizeDueTime;
                    _typeStdEnabled = stdCountdown.TypeStdEnabled;
                    _typeStdDueTime = stdCountdown.TypeStdDueTime;
                    UpdateCountdownText();      // 按最新快照立即重算文本（后续由 1 秒节拍维持秒级精度）
                });
            }
            catch (Exception ex)
            {
                // KPI 刷新失败经统一错误链上报（未捕获异常兜底码），不静默吞掉
                _errors.Collect(ErrorCodes.UnhandledException, "生产监控 KPI 刷新失败", null, ex);
            }
        });
    }

    // ==================== 任务队列 ====================

    /// <summary>队列全量快照（分页数据源；QueueItems 仅存当前页切片）。</summary>
    private List<QueueTaskItem> _allQueueItems = new();

    [ObservableProperty]
    private ObservableCollection<QueueTaskItem> _queueItems = new();

    /// <summary>队列是否有任务（控制空态说明与表格的可见性）。</summary>
    public bool HasQueueItems => _allQueueItems.Count > 0;

    /// <summary>队列是否多页（超过一页时显示分页脚）。</summary>
    public bool HasQueueMultiPage => QueueTotalPages > 1;

    /// <summary>队列当前页码（从 1 起）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueuePageDisplay))]
    [NotifyPropertyChangedFor(nameof(HasQueuePrevPage))]
    [NotifyPropertyChangedFor(nameof(HasQueueNextPage))]
    [NotifyCanExecuteChangedFor(nameof(FirstQueuePageCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrevQueuePageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextQueuePageCommand))]
    private int _queuePage = 1;

    /// <summary>队列总页数（至少 1 页）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueuePageDisplay))]
    [NotifyPropertyChangedFor(nameof(HasQueueMultiPage))]
    [NotifyPropertyChangedFor(nameof(HasQueueNextPage))]
    [NotifyCanExecuteChangedFor(nameof(NextQueuePageCommand))]
    private int _queueTotalPages = 1;

    /// <summary>队列总条数（标题"排队 N 个任务"）。</summary>
    [ObservableProperty]
    private int _queueTotalCount;

    /// <summary>队列分页栏显示文本。</summary>
    public string QueuePageDisplay => $"第 {QueuePage} / {QueueTotalPages} 页";

    public bool HasQueuePrevPage => QueuePage > 1;

    public bool HasQueueNextPage => QueuePage < QueueTotalPages;

    /// <summary>队列分页脚跳页输入框。</summary>
    [ObservableProperty]
    private string _queuePageJumpInput = string.Empty;

    /// <summary>页码变化时重切当前页切片。</summary>
    partial void OnQueuePageChanged(int value) => ShowQueuePage(value);

    /// <summary>刷新任务队列快照：仅等待（排队中）与进行中（执行中）任务，入队时间正序；StateChanged/导航时同步。</summary>
    private void RefreshQueue()
    {
        string TypeText(string taskType) => taskType switch
        {
            SctTaskType.Standardize => "标准化",
            SctTaskType.ControlCheck => "控制检查",
            SctTaskType.TypeStandardize => "类型标准化",
            SctTaskType.HeightMeasure => "测高",
            SctTaskType.Production => "生产样分析",
            SctTaskType.Archive => "归档",
            _ => taskType,
        };

        // SampleID → 样品名称映射（SCT 档案优先，生产样兜底；队列行展示用，每次刷新重建）
        var nameMap = new Dictionary<string, string>();
        foreach (var s in _samples.GetAll())
        {
            nameMap[s.SampleId] = s.SampleName;
        }

        foreach (var p in _production.GetAll())
        {
            nameMap[p.SampleId] = p.SampleName;
        }

        // SampleID → 合金类型映射（生产样必填合金；SCT 样仅类型标准化样有值；队列行展示用）
        var alloyMap = new Dictionary<string, string?>();
        foreach (var s in _samples.GetAll())
        {
            alloyMap[s.SampleId] = s.AlloyType;
        }

        foreach (var p in _production.GetAll())
        {
            alloyMap[p.SampleId] = p.AlloyType;
        }

        // 只显示等待（排队中）与进行中（执行中）的任务，已完成/失败/已取消不进队列（历史看板归日志页）；
        // 排序与引擎出队一致：优先级档位 → 手动置顶键/入队时间（手动调整只影响同档位次序）
        var items = _engine.GetRecentTasks()
            .Where(t => t.Status is SctTaskStatus.Queued or SctTaskStatus.Running)
            .OrderBy(t => t.Priority)
            .ThenBy(t => t.ManualSortKey ?? t.EnqueueTime)
            .Select(t => new QueueTaskItem
        {
            TaskId = t.TaskId,
            TaskIdText = $"#{t.TaskId}",
            TaskTypeText = TypeText(t.TaskType),
            SampleIdText = t.SampleId ?? "—",
            SampleNameText = t.SampleId is not null && nameMap.TryGetValue(t.SampleId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : "—",
            AlloyTypeText = t.SampleId is not null && alloyMap.TryGetValue(t.SampleId, out var alloy) && !string.IsNullOrWhiteSpace(alloy) ? alloy : "—",
            ReasonText = t.Reason ?? "—",
            EnqueueTimeText = t.Status == SctTaskStatus.Running ? string.Empty : t.EnqueueTime.ToString("yyyy-MM-dd HH:mm:ss"),
            IsRunning = t.Status == SctTaskStatus.Running,
            CanMoveTop = t.Status == SctTaskStatus.Queued && t.TaskType == SctTaskType.Production,
            CanRemove = t.Status == SctTaskStatus.Queued,
            StatusText = t.Status switch
            {
                SctTaskStatus.Queued => "排队中",
                SctTaskStatus.Running => "执行中",
                SctTaskStatus.Completed => "已完成",
                SctTaskStatus.Failed => "失败",
                SctTaskStatus.Cancelled => "已取消",
                _ => t.Status.ToString(),
            },
        }).ToList();

        _ui.Invoke(() =>
        {
            _allQueueItems = items;
            QueueTotalCount = items.Count;
            QueueTotalPages = Math.Max(1, (int)Math.Ceiling(items.Count / (double)QueuePageSize));
            // 越界收敛（总数变少时当前页可能超出末页；页码变化经 OnQueuePageChanged 自动重切）
            if (QueuePage > QueueTotalPages)
            {
                QueuePage = QueueTotalPages;
            }

            ShowQueuePage(QueuePage);
            OnPropertyChanged(nameof(HasQueueItems));
        });
    }

    /// <summary>内存切片显示队列指定页（快照已按优先级序）。</summary>
    private void ShowQueuePage(int page)
    {
        QueueItems = new ObservableCollection<QueueTaskItem>(
            _allQueueItems.Skip((page - 1) * QueuePageSize).Take(QueuePageSize));
    }

    /// <summary>队列回到首页（已在第 1 页时禁用）。</summary>
    [RelayCommand(CanExecute = nameof(HasQueuePrevPage))]
    private void FirstQueuePage() => QueuePage = 1;

    /// <summary>队列上一页（第 1 页时禁用）。</summary>
    [RelayCommand(CanExecute = nameof(HasQueuePrevPage))]
    private void PrevQueuePage() => QueuePage--;

    /// <summary>队列下一页（末页时禁用）。</summary>
    [RelayCommand(CanExecute = nameof(HasQueueNextPage))]
    private void NextQueuePage() => QueuePage++;

    /// <summary>队列分页脚「跳转」（解析失败忽略不清空，便于修正）。</summary>
    [RelayCommand]
    private void JumpToQueuePage()
    {
        if (!int.TryParse(QueuePageJumpInput?.Trim(), out var page))
        {
            return;
        }

        QueuePageJumpInput = string.Empty;
        QueuePage = Math.Clamp(page, 1, QueueTotalPages);
    }

    /// <summary>手动调整任务优先级：把等待中的生产任务在队列内置顶（引擎同档生效，留痕全流程日志），成功后即时刷新队列。</summary>
    [RelayCommand]
    private void MoveTaskTop(int taskId)
    {
        if (!_engine.MoveToTop(taskId))
        {
            return; // 非排队中任务（已执行/已终结）：引擎拒绝，界面刷新后按钮随 CanMoveTop 隐藏
        }

        RefreshQueue();
    }

    /// <summary>删除排队任务：二次确认后仅从队列移除（置已取消留痕，不删除样品档案与数据），成功后即时刷新队列。</summary>
    [RelayCommand]
    private void RemoveQueuedTask(int taskId)
    {
        var row = _allQueueItems.FirstOrDefault(i => i.TaskId == taskId);
        if (!_dialogs.Confirm("删除排队任务",
                $"确认将任务 {row?.TaskIdText ?? $"#{taskId}"}（样品 {row?.SampleIdText ?? "—"}）从队列移除？\n仅移除排队任务，不删除样品档案与数据。"))
        {
            return;
        }

        if (!_engine.RemoveQueuedTask(taskId))
        {
            return; // 非排队中任务（已执行/已终结）：引擎拒绝，刷新后按钮随 CanRemove 隐藏
        }

        RefreshQueue();
    }

    // ==================== 样品注册（弹窗） ====================

    /// <summary>打开样品注册弹窗：构建表单 VM（提交委托=注册服务）→ 模态弹窗；注册成功后刷新队列与 KPI。</summary>
    [RelayCommand]
    private void OpenSampleRegister()
    {
        // 提交委托：表单 → 注册表单数据 → 注册服务（校验/查重/入库/自动入队）
        var form = new SampleRegisterFormViewModel(async vm =>
        {
            try
            {
                return await _registration.RegisterAsync(vm.ToForm());
            }
            catch (Exception ex)
            {
                // 注册服务未捕获异常兜底（统一错误链，不静默吞掉）
                _errors.Collect(ErrorCodes.UnhandledException, "样品注册失败", vm.SampleId, ex);
                return "注册失败，请稍后重试";
            }
        });

        // 注册前加载当前可用（空闲）架位：下拉选择代替手输，占用来自标样库与生产样
        form.AvailableRackPositions = SampleManager.RackDirectory.AvailablePositions(
            _samples.GetAll(), _production.GetAll());

        if (_dialogs.ShowSampleRegister(form))
        {
            // 注册成功：入库与入队已完成，刷新队列卡与 KPI（StateChanged 亦会触发，此处即时同步）
            RefreshQueue();
            RefreshKpis();
            // 注册即写日志（入队审计），生产日志表同步重载，无需切页才更新
            LoadCommand.Execute(null);
        }
    }

    // ==================== 生产日志跳转样品档案 ====================

    /// <summary>跳转样品档案请求（MainViewModel 订阅后导航到样品档案页；参数=样品编号 + 日志完成时间）。</summary>
    public event Action<string, DateTime>? SampleLocateRequested;

    /// <summary>双击生产日志行：跳转样品档案定位对应样品（编号过滤选中 + 时间就近匹配高亮）。</summary>
    [RelayCommand]
    private void OpenLogSample(OperationLogRowItem? row)
    {
        if (row is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.RawSampleId))
        {
            _dialogs.Alert("无法跳转", "该日志行未关联样品编号，无法在样品档案中定位。");
            return;
        }

        if (!DateTime.TryParse(row.RawOperationTime, out var time))
        {
            _dialogs.Alert("无法跳转", "该日志行缺少有效完成时间，无法进行时间匹配。");
            return;
        }

        SampleLocateRequested?.Invoke(row.RawSampleId, time);
    }

    public ProductionMonitorViewModel(ISctOperationLogRepository logs, ISctSampleRepository samples,
        IProductionSampleRepository production, ISctStrategyEngine engine, IWorkflowOrchestrator orchestrator,
        IUiDispatcher ui, IErrorCollector errors, Ui.IDialogService dialogs,
        SampleManager.ISampleRegistrationService registration,
        SampleManager.ISampleDataChangeNotifier sampleDataChanged)
    {
        _logs = logs;
        _samples = samples;
        _production = production;
        _engine = engine;
        _orchestrator = orchestrator;
        _ui = ui;
        _errors = errors;
        _dialogs = dialogs;
        _registration = registration;
        _sampleDataChanged = sampleDataChanged;
        // 编排器取走任务/状态变化时同步刷新队列卡与 KPI（否则注册后任务瞬间被主循环出队，面板看似"没刷新"）
        _orchestrator.StateChanged += (_, _) =>
        {
            RefreshQueue();
            RefreshKpis();
        };
        // 任务执行链回喂（结果归档/档案更新/检查样插入完成等）→ 生产日志表即时重载（IsLoading 防重入）。
        // 此前本页漏订阅该通知，日志表只在导航进入时加载一次，检查样到期插入完成后不更新
        _sampleDataChanged.SampleDataChanged += () => _ = LoadCommand.ExecuteAsync(null);
    }

    /// <summary>导航进入时加载（导航路径为 fire-and-forget；测试可 await LoadCommand.ExecuteAsync 保证确定性）。</summary>
    public void OnNavigatedTo()
    {
        RefreshQueue();
        RefreshKpis();
        LoadCommand.Execute(null);
        StartKpiTimer();
    }
    /// <summary>分页大小（运行日志表每页行数）。</summary>
    public const int PageSize = 10;

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
    [NotifyPropertyChangedFor(nameof(HasPrevPage))]
    [NotifyPropertyChangedFor(nameof(HasNextPage))]
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

    /// <summary>类型筛选键（All/SettingUp/Control/TypeStd/Production；变化后回第 1 页重切）。</summary>
    [ObservableProperty]
    private string _filterKey = "All";

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
            // SampleID → 合金映射（一次构建，避免 7 万行日志逐行查询档案表的 O(n×m) 性能回归）
            var alloyMap = await Task.Run(() =>
            {
                var map = new Dictionary<string, string?>();
                foreach (var p in _production.GetAll())
                {
                    map[p.SampleId] = p.AlloyType;
                }

                foreach (var s in _samples.GetAll())
                {
                    if (!map.ContainsKey(s.SampleId))
                    {
                        map[s.SampleId] = s.AlloyType;
                    }
                }

                return (IReadOnlyDictionary<string, string?>)map;
            });
            _allLogs = await Task.Run(() => _logs.GetAll()
                .Select(l => MapLogRow(l, alloyMap))
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
    private OperationLogRowItem MapLogRow(SctOperationLog l, IReadOnlyDictionary<string, string?> alloyMap)
    {
        var time = DateTime.TryParse(l.OperationTime, out var t) ? t : DateTime.MinValue;
        var alloy = l.SampleId is not null && alloyMap.TryGetValue(l.SampleId, out var a) ? a : null;

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
                "TypeStandardize" => "类型标准化",
                _ => l.OperationType,
            },
            AlloyTypeText = !string.IsNullOrWhiteSpace(alloy) ? alloy : "—",
            TriggerReasonText = l.TriggerReason ?? "—",
            StartTimeText = DateTime.TryParse(l.StartTime, out var st) ? st.ToString("yyyy-MM-dd HH:mm:ss") : "—",
            OperationTimeText = time.ToString("yyyy-MM-dd HH:mm:ss"),
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
