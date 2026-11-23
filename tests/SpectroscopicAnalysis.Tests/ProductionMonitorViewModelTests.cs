using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.SampleManager;
using SpectroscopicAnalysis.Host.SctStrategy;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>生产监控 ViewModel 测试：样品注册/队列/KPI 刷新 + 运行日志加载/分页（日志模块已迁入本页）。</summary>
public sealed class ProductionMonitorViewModelTests
{
    private static ProductionMonitorViewModel CreateVm(
        FakeDialogService? dialogs = null,
        FakeSampleRegistrationService? registration = null,
        FakeSctOperationLogRepository? logs = null,
        FakeSctStrategyEngine? engine = null)
    {
        var dialogSvc = dialogs ?? new FakeDialogService();
        var regSvc = registration ?? new FakeSampleRegistrationService();
        return new ProductionMonitorViewModel(logs ?? new FakeSctOperationLogRepository(), new FakeSampleRepository(),
            new FakeProductionSampleRepository(), engine ?? new FakeSctStrategyEngine(), new FakeWorkflowOrchestrator(),
            new FakeUiDispatcher(), new FakeErrorCollector(), dialogSvc, regSvc, new SampleDataChangeNotifier());
    }

    [Fact]
    public void OpenSampleRegister_弹出注册表单并传入提交委托()
    {
        var dialogs = new FakeDialogService();
        var vm = CreateVm(dialogs: dialogs);

        vm.OpenSampleRegisterCommand.Execute(null);

        Assert.Single(dialogs.RegisterForms);       // 弹窗打开且表单 VM 传入
    }

    [Fact]
    public async Task OpenSampleRegister_确认入库后刷新队列与KPI()
    {
        var dialogs = new FakeDialogService();
        // 用真实注册服务（Fake 仓储）+ 真实策略引擎：注册入库并自动入队后，队列/KPI 可断言任务数
        var productionRepo = new FakeProductionSampleRepository();
        var samplesRepo = new FakeSampleRepository();
        var engine = new SctStrategyEngine(new FakeSctPolicyStore(), samplesRepo,
            new FakeSctOperationLogRepository(), new FakeErrorCollector());
        var registration = new SampleRegistrationService(samplesRepo, productionRepo, engine, new FakeErrorCollector(), new SystemLogCollector());
        var vm = new ProductionMonitorViewModel(new FakeSctOperationLogRepository(), samplesRepo, productionRepo, engine,
            new FakeWorkflowOrchestrator(), new FakeUiDispatcher(), new FakeErrorCollector(), dialogs, registration, new SampleDataChangeNotifier());

        // 模拟模态弹窗交互：弹窗显示期间填写表单并点确认入库（真实弹窗确认发生在返回之前）
        dialogs.OnShown = async form =>
        {
            form.SampleId = "P-100";
            form.SampleName = "测试样品";
            form.AnalysisMethod = "GB/T 7999-A";
            await form.ConfirmCommand.ExecuteAsync(null);
        };

        vm.OpenSampleRegisterCommand.Execute(null);
        await WaitQueueAsync(vm);                   // 队列/KPI 为 fire-and-forget，轮询等待两者都落到 UI

        var form = dialogs.RegisterForms.Single();
        Assert.True(form.Confirmed);                // 注册成功
        Assert.Single(productionRepo.Store);        // 生产样入库
        Assert.Equal(1, vm.QueueTotalCount);        // 注册自动入队后队列卡刷新
        Assert.Equal(1, vm.QueuePendingCount);      // KPI 待分析同步刷新
    }

    [Fact]
    public async Task Confirm_注册服务报错时留在弹窗显示错误()
    {
        var dialogs = new FakeDialogService();
        var registration = new FakeSampleRegistrationService { NextError = "样品编号已存在" };
        var vm = CreateVm(dialogs: dialogs, registration: registration);

        vm.OpenSampleRegisterCommand.Execute(null);
        var form = dialogs.RegisterForms[0];
        // 先通过字段级校验（必填项补全），错误来自服务端（Fake 预设）
        form.SampleId = "P-ERR";
        form.SampleName = "测试样品";
        form.AnalysisMethod = "GB/T 7999-A";
        await form.ConfirmCommand.ExecuteAsync(null);

        Assert.False(form.Confirmed);
        Assert.Null(form.SampleIdError);            // 字段级校验已通过，错误来自服务端
        Assert.Equal("样品编号已存在", form.RegisterError);
    }

    /// <summary>等待 fire-and-forget 的队列/KPI 刷新完成（后台取数 + Dispatcher 回 UI，轮询直到两者非默认值或超时）。</summary>
    [Fact]
    public async Task Load_任务队列_仅显示等待与进行中且按时间正序()
    {
        var engine = new FakeSctStrategyEngine();
        engine.RecentTasks.AddRange(
        [
            MakeTask(1, "P-003", SctTaskStatus.Queued, new DateTime(2026, 11, 20, 9, 10, 0)),
            MakeTask(2, "P-001", SctTaskStatus.Running, new DateTime(2026, 11, 20, 9, 9, 0)),
            MakeTask(3, "P-004", SctTaskStatus.Queued, new DateTime(2026, 11, 20, 9, 11, 0)),
            MakeTask(4, "P-002", SctTaskStatus.Completed, new DateTime(2026, 11, 20, 9, 8, 0)),   // 已完成不显示
            MakeTask(5, "P-005", SctTaskStatus.Cancelled, new DateTime(2026, 11, 20, 9, 7, 0)),   // 已取消不显示
        ]);
        var vm = CreateVm(engine: engine);

        vm.OnNavigatedTo();     // 队列刷新在导航进入时同步执行

        Assert.Equal(3, vm.QueueItems.Count);                                   // 过滤后仅 3 条
        Assert.Equal(["#2", "#1", "#3"], vm.QueueItems.Select(q => q.TaskIdText));   // 入队时间正序（执行中也在序列内）
        Assert.DoesNotContain(vm.QueueItems, q => q.StatusText == "已完成");
    }

    private static SctTask MakeTask(int id, string sampleId, string status, DateTime enqueueTime) => new()
    {
        TaskId = id,
        TaskType = SctTaskType.Production,
        SampleId = sampleId,
        Reason = "样品注册自动入队",
        EnqueueTime = enqueueTime,
        Status = status,
    };

    /// <summary>等待 fire-and-forget 的队列/KPI 刷新完成（后台取数 + Dispatcher 回 UI，轮询直到两者非默认值或超时）。</summary>
    private static async Task WaitQueueAsync(ProductionMonitorViewModel vm)
    {
        for (var i = 0; i < 100 && (vm.QueueTotalCount == 0 || vm.QueuePendingCount == 0); i++)
        {
            await Task.Delay(10);
        }
    }
    [Fact]
    public async Task RefreshKpis_今日已完成_按完成样品与当日结果统计()
    {
        var productionRepo = new FakeProductionSampleRepository();
        var today = DateTime.Today;
        // 完成 + 当日结果：计入；待分析：不计入；完成但结果在昨日：不计入
        productionRepo.Store.Add(new ProductionSample { SampleId = "P-1", SampleName = "a", SampleType = "生产", Status = ProductionStatus.Completed, CreatedTime = today.AddHours(-1) });
        productionRepo.Store.Add(new ProductionSample { SampleId = "P-2", SampleName = "b", SampleType = "生产", Status = ProductionStatus.Pending, CreatedTime = today.AddHours(-1) });
        productionRepo.Store.Add(new ProductionSample { SampleId = "P-3", SampleName = "c", SampleType = "生产", Status = ProductionStatus.Completed, CreatedTime = today.AddDays(-1) });
        productionRepo.Results.Add(new AnalysisResult { SampleId = "P-1", IsPass = true, ResultStatus = AnalysisResultStatus.Success, OperationTime = today.AddHours(8) });
        productionRepo.Results.Add(new AnalysisResult { SampleId = "P-3", IsPass = true, ResultStatus = AnalysisResultStatus.Success, OperationTime = today.AddDays(-1).AddHours(8) });
        // 显式传入同一仓储实例（CreateVm 默认自建实例，见不到本测试造的数据）
        var vm = new ProductionMonitorViewModel(new FakeSctOperationLogRepository(), new FakeSampleRepository(),
            productionRepo, new FakeSctStrategyEngine(), new FakeWorkflowOrchestrator(),
            new FakeUiDispatcher(), new FakeErrorCollector(), new FakeDialogService(), new FakeSampleRegistrationService(), new SampleDataChangeNotifier());

        vm.OnNavigatedTo();
        for (var i = 0; i < 100 && vm.TodayCompletedCount == 0; i++)
        {
            await Task.Delay(10);       // KPI 为 fire-and-forget，轮询等待落 UI
        }

        Assert.Equal(1, vm.TodayCompletedCount);
    }

    [Fact]
    public async Task RefreshKpis_按数量插入_标题切为计数()
    {
        var policy = new FakeSctPolicyStore
        {
            Current = new SctPolicyOptions { ControlInsertMode = ControlInsertMode.ByCount, ControlInsertCount = 15 },
        };
        var engine = new FakeSctStrategyEngine { Countdown = new SctControlCountdown { Mode = ControlInsertMode.ByCount, ProductionSinceControl = 3, ControlInsertCount = 15 } };
        var vm = new ProductionMonitorViewModel(new FakeSctOperationLogRepository(), new FakeSampleRepository(),
            new FakeProductionSampleRepository(), engine, new FakeWorkflowOrchestrator(),
            new FakeUiDispatcher(), new FakeErrorCollector(), new FakeDialogService(), new FakeSampleRegistrationService(), new SampleDataChangeNotifier());

        vm.OnNavigatedTo();
        for (var i = 0; i < 100 && vm.ControlCountdownText == "—"; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal("检查样插入计数", vm.ControlCountdownTitle);
        Assert.Equal("3 / 15", vm.ControlCountdownText);
    }

    [Fact]
    public async Task RefreshKpis_按时间插入_倒计时精确到秒()
    {
        var policy = new FakeSctPolicyStore
        {
            Current = new SctPolicyOptions { ControlInsertMode = ControlInsertMode.ByTime, ControlInsertMinutes = 60 },
        };
        var engine = new FakeSctStrategyEngine
        {
            Countdown = new SctControlCountdown
            {
                Mode = ControlInsertMode.ByTime,
                ControlInsertMinutes = 60,
                ControlDueTime = DateTime.Now.AddSeconds(90),   // 90 秒后到期
            },
        };
        var vm = new ProductionMonitorViewModel(new FakeSctOperationLogRepository(), new FakeSampleRepository(),
            new FakeProductionSampleRepository(), engine, new FakeWorkflowOrchestrator(),
            new FakeUiDispatcher(), new FakeErrorCollector(), new FakeDialogService(), new FakeSampleRegistrationService(), new SampleDataChangeNotifier());

        vm.OnNavigatedTo();
        for (var i = 0; i < 100 && vm.ControlCountdownText == "—"; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal("检查样插入倒计时", vm.ControlCountdownTitle);
        Assert.Matches(@"^剩余 \d{2}:\d{2}:\d{2}$", vm.ControlCountdownText);   // hh:mm:ss 秒级格式
    }

    [Fact]
    public async Task RefreshKpis_按时间无上次记录_显示到期待插入()
    {
        var policy = new FakeSctPolicyStore
        {
            Current = new SctPolicyOptions { ControlInsertMode = ControlInsertMode.ByTime, ControlInsertMinutes = 60 },
        };
        var engine = new FakeSctStrategyEngine
        {
            Countdown = new SctControlCountdown { Mode = ControlInsertMode.ByTime, ControlInsertMinutes = 60 },   // DueTime=null
        };
        var vm = new ProductionMonitorViewModel(new FakeSctOperationLogRepository(), new FakeSampleRepository(),
            new FakeProductionSampleRepository(), engine, new FakeWorkflowOrchestrator(),
            new FakeUiDispatcher(), new FakeErrorCollector(), new FakeDialogService(), new FakeSampleRegistrationService(), new SampleDataChangeNotifier());

        vm.OnNavigatedTo();
        for (var i = 0; i < 100 && vm.ControlCountdownText == "—"; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal("到期待插入", vm.ControlCountdownText);
    }

    [Fact]
    public async Task Load_日志进入表格_映射正确()
    {
        var vm = CreateWithLogs(
            MakeLog(1),
            MakeLog(2),
            MakeLog(3));

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.LogRows.Count);
        Assert.True(vm.HasLogs);
    }

    [Fact]
    public async Task Load_空库时无日志()
    {
        var vm = CreateWithLogs();
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Empty(vm.LogRows);
        Assert.False(vm.HasLogs);
        Assert.Equal(1, vm.TotalPages);
    }

    [Fact]
    public async Task Load_日志开始时间映射_有值显示原时间_空值显示占位()
    {
        var withStart = MakeLog(1);
        withStart.StartTime = "2026-11-20 08:05:00";        // 新日志：开始时间已采集
        var legacy = MakeLog(2);                             // 历史日志：StartTime 为 NULL
        var vm = CreateWithLogs(withStart, legacy);

        await vm.LoadCommand.ExecuteAsync(null);

        var newRow = vm.LogRows.Single(r => r.SampleIdText == "P-001");
        Assert.Equal("2026-11-20 08:05:00", newRow.StartTimeText);
        var legacyRow = vm.LogRows.Single(r => r.SampleIdText == "P-002");
        Assert.Equal("—", legacyRow.StartTimeText);
    }

    [Fact]
    public async Task Load_导航入口与加载命令一致()
    {
        var vm = CreateWithLogs(MakeLog(1));

        // OnNavigatedTo 为 fire-and-forget，与导航路径等价地直接 await 加载命令保证确定性
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Single(vm.LogRows);
    }

    [Fact]
    public async Task Load_按日期过滤_仅显当日日志()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var vm = CreateWithLogs(
            MakeLog(1, today),
            MakeLog(2, yesterday));

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Single(vm.LogRows);                              // 默认筛选今天，昨日日志不显示
        Assert.Equal("P-001", vm.LogRows[0].SampleIdText);
        Assert.Equal(1, vm.TotalCount);
        Assert.False(vm.HasNextPage);

        // 切到昨日日期：回第 1 页并只显示昨日日志（日期变更自动重查为 fire-and-forget，等待其空闲）
        vm.SelectedDate = DateTime.Today.AddDays(-1);
        await WaitIdleAsync(vm);

        Assert.Single(vm.LogRows);
        Assert.Equal("P-002", vm.LogRows[0].SampleIdText);
    }

    [Fact]
    public async Task NextPage_分页_取下一页数据()
    {
        var repo = new FakeSctOperationLogRepository();
        var baseTime = DateTime.Today;
        for (var i = 1; i <= 45; i++)
        {
            // 每条错开 1 分钟，保证倒序唯一：时间倒序后第 i 条为 seq 45-(i-1)
            var log = MakeLog(i);
            log.OperationTime = baseTime.AddMinutes(i).ToString("yyyy-MM-dd HH:mm:ss");
            repo.Store.Add(log);
        }

        var vm = CreateVm(logs: repo);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(10, vm.LogRows.Count);                     // 第 1 页 10 条
        Assert.Equal(45, vm.TotalCount);
        Assert.Equal(5, vm.TotalPages);
        Assert.True(vm.HasNextPage);
        Assert.Equal("P-045", vm.LogRows[0].SampleIdText);      // 时间倒序，最新在前
    }

    [Fact]
    public async Task NextPage_PrevPage_往返翻页正确()
    {
        var repo = new FakeSctOperationLogRepository();
        var baseTime = DateTime.Today;
        for (var i = 1; i <= 45; i++)
        {
            var log = MakeLog(i);
            log.OperationTime = baseTime.AddMinutes(i).ToString("yyyy-MM-dd HH:mm:ss");
            repo.Store.Add(log);
        }

        var vm = CreateVm(logs: repo);
        await vm.LoadCommand.ExecuteAsync(null);

        await InvokePagingAsync(vm.NextPageCommand);
        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal(10, vm.LogRows.Count);
        Assert.Equal("P-035", vm.LogRows[0].SampleIdText);      // 第二页首行
        Assert.True(vm.HasPrevPage);
        Assert.True(vm.HasNextPage);

        await InvokePagingAsync(vm.NextPageCommand);
        Assert.Equal(3, vm.CurrentPage);
        Assert.Equal("P-025", vm.LogRows[0].SampleIdText);

        await InvokePagingAsync(vm.NextPageCommand);
        await InvokePagingAsync(vm.NextPageCommand);
        Assert.Equal(5, vm.CurrentPage);
        Assert.Equal(5, vm.LogRows.Count);                      // 末页剩余 5 条
        Assert.False(vm.HasNextPage);

        await InvokePagingAsync(vm.PrevPageCommand);
        Assert.Equal(4, vm.CurrentPage);
        Assert.Equal("P-015", vm.LogRows[0].SampleIdText);      // 反向回到第四页

        await InvokePagingAsync(vm.FirstPageCommand);
        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal("P-045", vm.LogRows[0].SampleIdText);
        Assert.False(vm.HasPrevPage);
    }

    private static SctOperationLog MakeLog(int seq, string? date = null)
    {
        // 同一天内按分钟区分时间（倒序断言确定性）；date 为空默认今天
        var day = date ?? DateTime.Today.ToString("yyyy-MM-dd");
        return new SctOperationLog
        {
            LogId = seq,
            SampleId = $"P-{seq:000}",
            OperationType = seq % 3 == 0 ? "HeightMeasure" : "Standardization",
            TriggerReason = $"触发原因 {seq}",
            OperationTime = $"{day} 08:{seq % 60:00}:{seq % 60:00}",
            ResultStatus = seq % 7 == 0 ? AnalysisResultStatus.Warning : AnalysisResultStatus.Success,
        };
    }

    private static ProductionMonitorViewModel CreateWithLogs(params SctOperationLog[] logs)
    {
        var repo = new FakeSctOperationLogRepository();
        repo.Store.AddRange(logs);
        return CreateVm(logs: repo);
    }

    /// <summary>等待 fire-and-forget 重查完成（IsLoading 回落即表示本次加载结束）。</summary>
    private static async Task WaitIdleAsync(ProductionMonitorViewModel vm)
    {
        for (var i = 0; i < 100 && vm.IsLoading; i++)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>翻页命令为 fire-and-forget 异步路径；经 AsyncRelayCommand 的 ExecutionTask 等待其完成（确定性）。</summary>
    private static async Task InvokePagingAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        var async = command as CommunityToolkit.Mvvm.Input.IAsyncRelayCommand;
        if (async?.ExecutionTask is { IsCompleted: false } task)
        {
            await task;
        }
    }
}
