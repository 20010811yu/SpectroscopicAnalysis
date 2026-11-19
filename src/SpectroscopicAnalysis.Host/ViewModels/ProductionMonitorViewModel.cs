using System.Collections.ObjectModel;
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
    public required string TaskIdText { get; init; }
    public required string TaskTypeText { get; init; }
    public required string SampleIdText { get; init; }
    /// <summary>样品名称（SCT/生产样档案查询；无档案时显示 —）。</summary>
    public required string SampleNameText { get; init; }
    public required string ReasonText { get; init; }
    public required string EnqueueTimeText { get; init; }
    /// <summary>是否执行中（视图切绿色徽章）。</summary>
    public bool IsRunning { get; init; }
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

    private readonly ISctSampleRepository _samples;
    private readonly IProductionSampleRepository _production;
    private readonly ISctStrategyEngine _engine;
    private readonly IWorkflowOrchestrator _orchestrator;
    private readonly IUiDispatcher _ui;
    private readonly IErrorCollector _errors;
    private readonly Ui.IDialogService _dialogs;
    private readonly SampleManager.ISampleRegistrationService _registration;

    // ==================== KPI 四卡 ====================

    /// <summary>「今日已完成」数（Status=Completed 的生产样；有结果的按最新结果时间=今日过滤，无结果也计入）。</summary>
    [ObservableProperty]
    private int _todayCompletedCount;

    /// <summary>「队列待分析」数（引擎当前排队任务数，不含执行中与历史）。</summary>
    [ObservableProperty]
    private int _queuePendingCount;

    /// <summary>「未确认报警」数（ErrorCollector 内存报警表实时值）。</summary>
    [ObservableProperty]
    private int _unackAlarmCount;

    /// <summary>「控制样插入倒计时」文本（ByCount：n / 阈值；ByTime：已过分钟 / 间隔 分钟）。</summary>
    [ObservableProperty]
    private string _controlCountdownText = "—";

    /// <summary>KPI 定时刷新（导航后启动一次；页面为单例常驻，随宿主退出结束）。</summary>
    private PeriodicTimer? _kpiTimer;

    /// <summary>启动 KPI 定时刷新循环（重复导航防重启；取消/异常静默收尾——宿主关闭路径）。</summary>
    private void StartKpiTimer()
    {
        if (_kpiTimer is not null)
        {
            return;
        }

        _kpiTimer = new PeriodicTimer(KpiRefreshInterval);
        _ = RunKpiTimerAsync(_kpiTimer);
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
    /// 刷新 KPI 四卡：今日已完成（Status=Completed 生产样，无结果也计入）/ 队列待分析 / 未确认报警 / 控制样倒计时。
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
                var text = countdown.Mode == ControlInsertMode.ByTime
                    ? $"{(int)countdown.MinutesSinceControl} / {countdown.ControlInsertMinutes} 分钟"
                    : $"{countdown.ProductionSinceControl} / {countdown.ControlInsertCount}";

                _ui.Invoke(() =>
                {
                    TodayCompletedCount = completed;
                    QueuePendingCount = _engine.GetQueuedTasks().Count;
                    UnackAlarmCount = _errors.UnacknowledgedCount;
                    ControlCountdownText = text;
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

    /// <summary>刷新任务队列快照：执行中任务（编排器 Snapshot）置顶 + 引擎排队任务；StateChanged/导航时同步。</summary>
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

        var items = _engine.GetRecentTasks().Select(t => new QueueTaskItem
        {
            TaskIdText = $"#{t.TaskId}",
            TaskTypeText = TypeText(t.TaskType),
            SampleIdText = t.SampleId ?? "—",
            SampleNameText = t.SampleId is not null && nameMap.TryGetValue(t.SampleId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : "—",
            ReasonText = t.Reason ?? "—",
            EnqueueTimeText = t.Status == SctTaskStatus.Running ? string.Empty : t.EnqueueTime.ToString("yyyy-MM-dd HH:mm:ss"),
            IsRunning = t.Status == SctTaskStatus.Running,
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

        if (_dialogs.ShowSampleRegister(form))
        {
            // 注册成功：入库与入队已完成，刷新队列卡与 KPI（StateChanged 亦会触发，此处即时同步）
            RefreshQueue();
            RefreshKpis();
        }
    }

    public ProductionMonitorViewModel(ISctSampleRepository samples,
        IProductionSampleRepository production, ISctStrategyEngine engine, IWorkflowOrchestrator orchestrator,
        IUiDispatcher ui, IErrorCollector errors, Ui.IDialogService dialogs,
        SampleManager.ISampleRegistrationService registration)
    {
        _samples = samples;
        _production = production;
        _engine = engine;
        _orchestrator = orchestrator;
        _ui = ui;
        _errors = errors;
        _dialogs = dialogs;
        _registration = registration;
        // 编排器取走任务/状态变化时同步刷新队列卡与 KPI（否则注册后任务瞬间被主循环出队，面板看似"没刷新"）
        _orchestrator.StateChanged += (_, _) =>
        {
            RefreshQueue();
            RefreshKpis();
        };
    }

    /// <summary>导航进入时加载（导航路径为 fire-and-forget；测试可 await LoadCommand.ExecuteAsync 保证确定性）。</summary>
    public void OnNavigatedTo()
    {
        RefreshQueue();
        RefreshKpis();
        StartKpiTimer();
    }
}
