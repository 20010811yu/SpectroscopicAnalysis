using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.SctStrategy;
using SpectroscopicAnalysis.Host.Ui;

namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>
/// 主控台 ViewModel：报警面板 + 全流程运行日志 + 货架示意 + 激光打标面板（只读）+ GNR 光谱仪（静态占位）。
/// 数据源：ErrorCollector 内存报警表 / SystemLogCollector 全流程日志 / SCT+生产样档案架位；
/// 刷新走 30 秒 PeriodicTimer（模式同生产监控 KPI），I/O 在后台线程、结果经 IUiDispatcher 回 UI（开发规范第 4 节）。
/// </summary>
public sealed partial class ConsoleViewModel : ObservableObject, IPageLifecycle
{
    /// <summary>面板定时刷新周期（报警/日志/货架共用一轮）。</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>货架示意规格（暂定 5 行 × 8 列；行标识 A–E）。</summary>
    public const int RackRowsCount = 5;
    public const int RackColumnsCount = 8;
    private static readonly string[] RackRowLabels = { "A", "B", "C", "D", "E" };

    private readonly IErrorCollector _errors;
    private readonly ISystemLogCollector _systemLog;
    private readonly ISctSampleRepository _samples;
    private readonly IProductionSampleRepository _production;
    private readonly ISctPolicyStore _policyStore;
    private readonly IUiDispatcher _ui;
    private readonly LaserMarkingViewModel _laserMarking;

    private PeriodicTimer? _refreshTimer;

    public ConsoleViewModel(IErrorCollector errors, ISystemLogCollector systemLog,
        ISctSampleRepository samples, IProductionSampleRepository production,
        ISctPolicyStore policyStore, IUiDispatcher ui, LaserMarkingViewModel laserMarking)
    {
        _errors = errors;
        _systemLog = systemLog;
        _samples = samples;
        _production = production;
        _policyStore = policyStore;
        _ui = ui;
        _laserMarking = laserMarking;
        Laser = laserMarking;
        // 日志写入即时刷新（事件驱动；Log 可能在任意线程，经 IUiDispatcher 切回 UI 线程）
        systemLog.EntryLogged += () => _ui.Invoke(RefreshSystemLogs);
        // 报警产生即时刷新报警面板（掉线等报警不再滞后 30 秒定时刷新；Collect 可能在任意线程）
        errors.AlarmRaised += () => _ui.Invoke(RefreshAlarms);
    }

    // ==================== 报警面板 ====================

    /// <summary>未确认报警数（标题右侧实时计数）。</summary>
    public int UnackAlarmCount => _errors.UnacknowledgedCount;

    [ObservableProperty]
    private ObservableCollection<AlarmRowItem> _alarmRows = new();

    /// <summary>是否存在报警（控制占位说明与列表的可见性）。</summary>
    public bool HasAlarms => AlarmRows.Count > 0;

    /// <summary>是否存在日志记录（控制占位说明与列表的可见性）。</summary>
    public bool HasSystemLogs => SystemLogRows.Count > 0;

    /// <summary>确认单条报警（刷新列表与计数）。参数类型须与绑定源 AlarmId（long）一致——
    /// string 参数会在绑定激活时收到 Int64 抛 ArgumentException（UI 线程未处理异常即崩溃）。</summary>
    [RelayCommand]
    private void AckAlarm(long alarmId)
    {
        _errors.Acknowledge(alarmId);
        RefreshAlarms();
    }

    /// <summary>确认全部报警。</summary>
    [RelayCommand]
    private void AckAllAlarms()
    {
        _errors.AcknowledgeAll();
        RefreshAlarms();
    }

    /// <summary>激光打标掉线（9004）条目「重新加载」：一键重新拉起 EzCad2；
    /// 成功后自动确认该条掉线报警（故障已消除，无需人工再确认），失败保留报警由用户处理。参数类型同 AckAlarm（long）。</summary>
    [RelayCommand]
    private async Task ReloadLaserAsync(long alarmId)
    {
        if (_isReloadingLaser)
        {
            return;   // 防重入：重新拉起耗时数秒，期间忽略重复点击
        }

        _isReloadingLaser = true;
        try
        {
            var error = await Laser.RelaunchAsync();
            if (error is null)
            {
                _errors.Acknowledge(alarmId);
            }

            RefreshAlarms();
        }
        finally
        {
            _isReloadingLaser = false;
        }
    }

    /// <summary>激光打标重新加载执行中标志（防重入）。</summary>
    private bool _isReloadingLaser;

    /// <summary>刷新报警列表（调用方须已在 UI 线程；数据源为内存快照，直接同步映射）。</summary>
    private void RefreshAlarms()
    {
        AlarmRows = new ObservableCollection<AlarmRowItem>(_errors.GetAlarms()
            .Select(a => new AlarmRowItem
            {
                AlarmId = a.AlarmId,
                TimeText = a.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                ErrorCodeText = a.ErrorCode.ToString(),
                MessageText = a.Message,
                SampleIdText = string.IsNullOrEmpty(a.SampleId) ? "—" : a.SampleId,
                // 整行文案在 VM 拼接：无样品上下文的报警（如打标机掉线）不追加「（样品 …）」空占位
                DisplayText = $"✖ {a.Time:yyyy-MM-dd HH:mm:ss}  错误码 {a.ErrorCode}  " +
                              (string.IsNullOrEmpty(a.SampleId) ? a.Message : $"{a.Message}（样品 {a.SampleId}）"),
                IsAcknowledged = a.IsAcknowledged,
            }));
        OnPropertyChanged(nameof(HasAlarms));
        OnPropertyChanged(nameof(UnackAlarmCount));
    }

    // ==================== 全流程运行日志 ====================

    [ObservableProperty]
    private ObservableCollection<SystemLogRowItem> _systemLogRows = new();

    /// <summary>刷新全流程日志列表（内存快照，直接同步映射）。</summary>
    private void RefreshSystemLogs()
    {
        SystemLogRows = new ObservableCollection<SystemLogRowItem>(_systemLog.GetEntries()
            .Select(e => new SystemLogRowItem
            {
                TimeText = e.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                CategoryText = e.Category,
                MessageText = e.Message,
                Level = e.Level,
            }));
        OnPropertyChanged(nameof(HasSystemLogs));
    }

    // ==================== 货架示意（5×8） ====================

    /// <summary>示意范围内占用数。</summary>
    public int RackOccupiedCount => RackRows.Sum(r => r.Slots.Count(s => s.IsOccupied));

    /// <summary>架位总数（示意范围内）。</summary>
    public int RackTotalCount => RackRowsCount * RackColumnsCount;

    /// <summary>超出示意范围的架位占用数（如 A12 等）。</summary>
    public int RackOutOfRangeCount { get; private set; }

    [ObservableProperty]
    private ObservableCollection<RackRowItem> _rackRows = new();

    /// <summary>刷新货架占用（档案查询在后台线程，避免 UI 卡顿；公开供测试确定性刷新）。</summary>
    public void RefreshRack()
    {
        // SampleID → (样品号, 库位状态) 映射（SCT 标样优先，生产样兜底；注册时已保证架位唯一）。
        // 库位状态按样品状态区分：禁用=红、需测高=黄、可用=蓝；生产样无状态机，占用即视为可用。
        var occupancy = new Dictionary<string, (string SampleId, string State, string StateText)>();
        foreach (var s in _samples.GetAll())
        {
            if (string.IsNullOrEmpty(s.RackPosition))
            {
                continue;
            }

            var (state, text) = s.Status switch
            {
                SampleStatus.Disabled => (RackSlotItem.StateDisabled, "禁用"),
                SampleStatus.NeedMeasure => (RackSlotItem.StateNeedMeasure, "需测高"),
                _ => (RackSlotItem.StateNormal, "可用"),
            };
            occupancy.TryAdd(s.RackPosition, (s.SampleId, state, text));
        }
        foreach (var p in _production.GetAll())
        {
            if (!string.IsNullOrEmpty(p.RackPosition))
            {
                occupancy.TryAdd(p.RackPosition, (p.SampleId, RackSlotItem.StateNormal, "可用"));
            }
        }

        var rows = new List<RackRowItem>();
        foreach (var rowLabel in RackRowLabels)
        {
            var slots = new List<RackSlotItem>();
            for (var col = 1; col <= RackColumnsCount; col++)
            {
                var code = $"{rowLabel}{col:00}";
                slots.Add(occupancy.Remove(code, out var slot)
                    ? new RackSlotItem
                    {
                        PositionCode = code,
                        SampleIdText = slot.SampleId,
                        IsOccupied = true,
                        SlotState = slot.State,
                        StateText = slot.StateText,
                    }
                    : new RackSlotItem { PositionCode = code, SampleIdText = string.Empty, IsOccupied = false, SlotState = RackSlotItem.StateEmpty, StateText = "空位" });
            }
            rows.Add(new RackRowItem { RowLabel = rowLabel, Slots = slots });
        }
        var outOfRange = occupancy.Count;   // 剩余即示意范围外的架位

        _ui.Invoke(() =>
        {
            RackRows = new ObservableCollection<RackRowItem>(rows);
            RackOutOfRangeCount = outOfRange;
            OnPropertyChanged(nameof(RackOccupiedCount));
            OnPropertyChanged(nameof(RackTotalCount));
            OnPropertyChanged(nameof(RackOutOfRangeCount));
        });
    }

    // ==================== 激光打标面板（完整功能：方式切换/内容编辑/重新加载，原独立页面能力并入） ====================

    /// <summary>激光打标 ViewModel（DI 单例；面板直接绑定其属性与命令，编辑/切换即时生效）。</summary>
    public LaserMarkingViewModel Laser { get; }

    // ==================== 生命周期与定时刷新 ====================

    /// <summary>启动定时刷新循环（重复导航防重启；取消/异常静默收尾——宿主关闭路径）。</summary>
    private void StartRefreshTimer()
    {
        if (_refreshTimer is not null)
        {
            return;
        }

        _refreshTimer = new PeriodicTimer(RefreshInterval);
        _ = RunRefreshTimerAsync(_refreshTimer);
    }

    private async Task RunRefreshTimerAsync(PeriodicTimer timer)
    {
        try
        {
            while (await timer.WaitForNextTickAsync())
            {
                RefreshAll();
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主关闭取消定时器，属正常关闭路径
        }
    }

    /// <summary>刷新全部面板数据（须在 UI 线程调用：内存快照同步映射 + 货架后台查询）。</summary>
    private void RefreshAll()
    {
        RefreshAlarms();
        RefreshSystemLogs();
        RefreshPolicySummary();
        _ = Task.Run(RefreshRack);
    }

    /// <summary>导航进入时刷新一轮并启动定时器（打标内容随导航重载，保持与文件一致）。</summary>
    public void OnNavigatedTo()
    {
        RefreshAll();
        _laserMarking.OnNavigatedTo();
        StartRefreshTimer();
    }

    // ==================== SCT 当前策略摘要（当前任务卡底部展示） ====================

    /// <summary>检查样选择规则文案（最少使用优先/轮询，含多块轮换开关）。</summary>
    [ObservableProperty]
    private string _settingRuleText = string.Empty;

    /// <summary>检查样插入模式文案（按数量/按时间，带具体间隔值）。</summary>
    [ObservableProperty]
    private string _controlInsertText = string.Empty;

    /// <summary>超差重测上限文案。</summary>
    [ObservableProperty]
    private string _recheckLimitText = string.Empty;

    /// <summary>
    /// 刷新 SCT 当前策略摘要（JSON 配置 Load 同步读取，文件小、直接在 UI 线程执行）。
    /// 定时刷新兜底：SCT 管理页保存策略后主控台 30 秒内自动更新。
    /// </summary>
    private void RefreshPolicySummary()
    {
        var policy = _policyStore.Load();
        // 选样规则（SettingSampleSelectionMode 常量键 → 中文；轮换启用时追加说明）
        var rule = policy.SettingSampleSelectionMode == SettingSampleSelectionMode.RoundRobin
            ? "轮询"
            : "最少使用优先";
        SettingRuleText = policy.ControlRotationEnabled
            ? $"选样规则：{rule} · 多块轮换"
            : $"选样规则：{rule}";
        // 插入模式（带具体间隔值）
        ControlInsertText = policy.ControlInsertMode == ControlInsertMode.ByTime
            ? $"检查样插入：每 {policy.ControlInsertMinutes} 分钟 1 次"
            : $"检查样插入：每 {policy.ControlInsertCount} 个生产样 1 次";
        RecheckLimitText = $"超差重测：上限 {policy.ControlRecheckMaxCount} 次";
    }
}
