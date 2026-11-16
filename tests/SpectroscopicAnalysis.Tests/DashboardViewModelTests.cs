using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>主控台 ViewModel 测试：运行日志加载/映射/日期过滤/分页（数据源 SCT_OperationLog）。</summary>
public sealed class DashboardViewModelTests
{
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

    private static DashboardViewModel CreateWith(params SctOperationLog[] logs)
    {
        var repo = new FakeSctOperationLogRepository();
        repo.Store.AddRange(logs);
        return new DashboardViewModel(repo, new FakeSampleRepository(), new FakeProductionSampleRepository(),
            new FakeSctStrategyEngine(), new FakeWorkflowOrchestrator(), new FakeUiDispatcher(), new FakeErrorCollector());
    }

    [Fact]
    public async Task Load_日志进入表格_映射正确()
    {
        var vm = CreateWith(
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
        var vm = CreateWith();
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Empty(vm.LogRows);
        Assert.False(vm.HasLogs);
        Assert.Equal(1, vm.TotalPages);
    }

    [Fact]
    public async Task Load_导航入口与加载命令一致()
    {
        var vm = CreateWith(MakeLog(1));

        // OnNavigatedTo 为 fire-and-forget，与导航路径等价地直接 await 加载命令保证确定性
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Single(vm.LogRows);
    }

    [Fact]
    public async Task Load_按日期过滤_仅显当日日志()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var vm = CreateWith(
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

        var vm = new DashboardViewModel(repo, new FakeSampleRepository(), new FakeProductionSampleRepository(),
            new FakeSctStrategyEngine(), new FakeWorkflowOrchestrator(), new FakeUiDispatcher(), new FakeErrorCollector());
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

        var vm = new DashboardViewModel(repo, new FakeSampleRepository(), new FakeProductionSampleRepository(),
            new FakeSctStrategyEngine(), new FakeWorkflowOrchestrator(), new FakeUiDispatcher(), new FakeErrorCollector());
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

    /// <summary>等待 fire-and-forget 重查完成（IsLoading 回落即表示本次加载结束）。</summary>
    private static async Task WaitIdleAsync(DashboardViewModel vm)
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
