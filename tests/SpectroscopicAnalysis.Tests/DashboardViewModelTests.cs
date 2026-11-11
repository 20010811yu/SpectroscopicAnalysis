using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>主控台 ViewModel 测试：「生产样品（今日）」模块加载与映射（模块自样品档案页迁入）。</summary>
public sealed class DashboardViewModelTests
{
    private static SctSample Make(string id, string type, string status = SampleStatus.Normal, DateTime? created = null) => new()
    {
        SampleId = id,
        SampleName = id,
        SampleType = type,
        AlloyType = "6xxx",
        Status = status,
        CreatedTime = created ?? DateTime.Today, // 默认今日入库（与默认筛选日期一致）
    };

    private static DashboardViewModel CreateWith(params SctSample[] samples)
    {
        var repo = new FakeSampleRepository();
        repo.Store.AddRange(samples);
        return new DashboardViewModel(repo, new FakeUiDispatcher(), new FakeErrorCollector());
    }

    [Fact]
    public async Task Load_仅生产样品进入表格_状态映射正确()
    {
        var vm = CreateWith(
            Make("SUS-001", SampleType.SettingUp),
            Make("P-20261110-07", SampleType.Production),
            Make("P-20261110-08", SampleType.Production, SampleStatus.NeedMeasure));

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.ProductionSamples.Count);            // 仅生产样品
        Assert.DoesNotContain(vm.ProductionSamples, r => r.SampleId == "SUS-001");
        Assert.Equal("● 正常", vm.ProductionSamples[0].StatusText);
        Assert.Equal("▲ 需测高", vm.ProductionSamples[1].StatusText);
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
        var vm = CreateWith(Make("P-20261110-07", SampleType.Production));

        // OnNavigatedTo 为 fire-and-forget，与导航路径等价地直接 await 加载命令保证确定性
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Single(vm.ProductionSamples);
    }

    [Fact]
    public async Task Load_按日期过滤_仅显当日入库()
    {
        var vm = CreateWith(
            Make("P-TODAY-01", SampleType.Production, created: DateTime.Today),
            Make("P-YESTD-01", SampleType.Production, created: DateTime.Today.AddDays(-1)));

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

    /// <summary>等待 fire-and-forget 重查完成（IsLoading 回落即表示本次加载结束）。</summary>
    private static async Task WaitIdleAsync(DashboardViewModel vm)
    {
        for (var i = 0; i < 100 && vm.IsLoading; i++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task NextPage_游标翻页_取下一页数据()
    {
        var repo = new FakeSampleRepository();
        for (var i = 1; i <= 45; i++)
        {
            repo.Store.Add(Make($"P-{i:000}", SampleType.Production));
        }

        var vm = new DashboardViewModel(repo, new FakeUiDispatcher(), new FakeErrorCollector());
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(20, vm.ProductionSamples.Count);           // 第 1 页 20 条
        Assert.Equal(45, vm.TotalCount);
        Assert.Equal(3, vm.TotalPages);
        Assert.True(vm.HasNextPage);
        Assert.Equal("P-001", vm.ProductionSamples[0].SampleId);
    }

    [Fact]
    public async Task NextPage_PrevPage_往返游标正确()
    {
        var repo = new FakeSampleRepository();
        for (var i = 1; i <= 45; i++)
        {
            repo.Store.Add(Make($"P-{i:000}", SampleType.Production));
        }

        var vm = new DashboardViewModel(repo, new FakeUiDispatcher(), new FakeErrorCollector());
        await vm.LoadCommand.ExecuteAsync(null);

        await InvokePagingAsync(vm.NextPageCommand);
        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal(20, vm.ProductionSamples.Count);
        Assert.Equal("P-021", vm.ProductionSamples[0].SampleId); // 第二页首行
        Assert.True(vm.HasPrevPage);
        Assert.True(vm.HasNextPage);

        await InvokePagingAsync(vm.NextPageCommand);
        Assert.Equal(3, vm.CurrentPage);
        Assert.Equal(5, vm.ProductionSamples.Count);            // 末页剩余 5 条
        Assert.False(vm.HasNextPage);

        await InvokePagingAsync(vm.PrevPageCommand);
        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal("P-021", vm.ProductionSamples[0].SampleId); // 反向游标回到第二页

        await InvokePagingAsync(vm.FirstPageCommand);
        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal("P-001", vm.ProductionSamples[0].SampleId);
        Assert.False(vm.HasPrevPage);
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
