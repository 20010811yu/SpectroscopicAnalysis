using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>主控台 ViewModel 测试：「生产样品（今日）」模块加载与映射（模块自样品档案页迁入）。</summary>
public sealed class DashboardViewModelTests
{
    private static SctSample Make(string id, string type, string status = SampleStatus.Normal) => new()
    {
        SampleId = id,
        SampleName = id,
        SampleType = type,
        AlloyType = "6xxx",
        Status = status,
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
}
