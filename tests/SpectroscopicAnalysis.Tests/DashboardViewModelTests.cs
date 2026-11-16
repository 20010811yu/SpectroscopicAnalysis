using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>主控台 ViewModel 测试：生产样表格加载/映射/日期过滤/游标分页（数据源 Production_Sample）。</summary>
public sealed class DashboardViewModelTests
{
    private static ProductionSample Make(string id, string status = ProductionStatus.Normal, DateTime? created = null) => new()
    {
        SampleId = id,
        SampleName = $"生产样 {id}",
        AlloyType = "6xxx",
        RackPosition = "D01",
        Status = status,
        CreatedTime = created ?? DateTime.Today, // 默认今日入库（与默认筛选日期一致）
    };

    private static DashboardViewModel CreateWith(params ProductionSample[] samples)
    {
        var repo = new FakeProductionSampleRepository();
        repo.Store.AddRange(samples);
        return new DashboardViewModel(repo, new FakeUiDispatcher(), new FakeErrorCollector());
    }

    [Fact]
    public async Task Load_生产样进入表格_状态映射正确()
    {
        var vm = CreateWith(
            Make("P-001", ProductionStatus.Normal),
            Make("P-002", ProductionStatus.Analyzing),
            Make("P-003", ProductionStatus.Completed),
            Make("P-004", ProductionStatus.Failed));

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.ProductionSamples.Count);
        Assert.Equal("✖ 失败", vm.ProductionSamples[0].StatusText); // 时间倒序：同入库时间按 SampleID 倒序
        Assert.Equal("✓ 已完成", vm.ProductionSamples[1].StatusText);
        Assert.Equal("● 分析中", vm.ProductionSamples[2].StatusText);
        Assert.Equal("● 正常", vm.ProductionSamples[3].StatusText);
        Assert.Equal("生产样 P-004", vm.ProductionSamples[0].SampleName); // 名称列映射
        Assert.True(vm.HasProduction);
    }

    [Fact]
    public async Task Load_空库时无生产样品()
    {
        var vm = CreateWith();
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Empty(vm.ProductionSamples);
        Assert.False(vm.HasProduction);
    }

    [Fact]
    public async Task Load_导航入口与加载命令一致()
    {
        var vm = CreateWith(Make("P-001"));

        // OnNavigatedTo 为 fire-and-forget，与导航路径等价地直接 await 加载命令保证确定性
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Single(vm.ProductionSamples);
    }

    [Fact]
    public async Task Load_按日期过滤_仅显当日入库()
    {
        var vm = CreateWith(
            Make("P-TODAY-01", created: DateTime.Today),
            Make("P-YESTD-01", created: DateTime.Today.AddDays(-1)));

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Single(vm.ProductionSamples);                    // 默认筛选今天，昨日入库不显示
        Assert.Equal("P-TODAY-01", vm.ProductionSamples[0].SampleId);
        Assert.Equal(1, vm.TotalCount);
        Assert.False(vm.HasNextPage);

        // 切到昨日日期：回第 1 页并只显示昨日数据（日期变更自动重查为 fire-and-forget，等待其空闲）
        vm.SelectedDate = DateTime.Today.AddDays(-1);
        await WaitIdleAsync(vm);

        Assert.Single(vm.ProductionSamples);
        Assert.Equal("P-YESTD-01", vm.ProductionSamples[0].SampleId);
    }

    [Fact]
    public async Task NextPage_游标翻页_取下一页数据()
    {
        var repo = new FakeProductionSampleRepository();
        for (var i = 1; i <= 45; i++)
        {
            repo.Store.Add(Make($"P-{i:000}"));
        }

        var vm = new DashboardViewModel(repo, new FakeUiDispatcher(), new FakeErrorCollector());
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(10, vm.ProductionSamples.Count);           // 第 1 页 10 条
        Assert.Equal(45, vm.TotalCount);
        Assert.Equal(5, vm.TotalPages);
        Assert.True(vm.HasNextPage);
        Assert.Equal("P-045", vm.ProductionSamples[0].SampleId); // 时间倒序，最新在前
    }

    [Fact]
    public async Task NextPage_PrevPage_往返游标正确()
    {
        var repo = new FakeProductionSampleRepository();
        for (var i = 1; i <= 45; i++)
        {
            repo.Store.Add(Make($"P-{i:000}"));
        }

        var vm = new DashboardViewModel(repo, new FakeUiDispatcher(), new FakeErrorCollector());
        await vm.LoadCommand.ExecuteAsync(null);

        await InvokePagingAsync(vm.NextPageCommand);
        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal(10, vm.ProductionSamples.Count);
        Assert.Equal("P-035", vm.ProductionSamples[0].SampleId); // 第二页首行
        Assert.True(vm.HasPrevPage);
        Assert.True(vm.HasNextPage);

        await InvokePagingAsync(vm.NextPageCommand);
        Assert.Equal(3, vm.CurrentPage);
        Assert.Equal(10, vm.ProductionSamples.Count);
        Assert.Equal("P-025", vm.ProductionSamples[0].SampleId);

        await InvokePagingAsync(vm.NextPageCommand);
        await InvokePagingAsync(vm.NextPageCommand);
        Assert.Equal(5, vm.CurrentPage);
        Assert.Equal(5, vm.ProductionSamples.Count);            // 末页剩余 5 条
        Assert.False(vm.HasNextPage);

        await InvokePagingAsync(vm.PrevPageCommand);
        Assert.Equal(4, vm.CurrentPage);
        Assert.Equal("P-015", vm.ProductionSamples[0].SampleId); // 反向游标回到第四页

        await InvokePagingAsync(vm.FirstPageCommand);
        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal("P-045", vm.ProductionSamples[0].SampleId);
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
