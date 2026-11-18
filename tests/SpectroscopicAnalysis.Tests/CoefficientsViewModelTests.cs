using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>系数管理 ViewModel 测试：加载/筛选/分页/Tab 切换/变更历史过滤（数据源 CorrectionCoefficient + SCT_OperationLog）。</summary>
public sealed class CoefficientsViewModelTests
{
    private static CorrectionCoefficient MakeCoeff(
        string method = "GB/T 7999-A", string element = "Si", int version = 125,
        bool isCurrent = true, string time = "2026-01-01 08:00:00", string? source = "SUS-001") => new()
    {
        CoeffId = Random.Shared.NextInt64(),
        MethodId = method,
        Element = element,
        Slope = 1.234567,
        Intercept = 0.001,
        EffectiveTime = time,
        SourceSampleId = source,
        IsCurrent = isCurrent,
        Version = version,
    };

    private static SctOperationLog MakeLog(
        int seq, string? coeffBefore = null, string? coeffAfter = null) => new()
    {
        LogId = seq,
        SampleId = $"SUS-{seq:000}",
        OperationType = "Standardization",
        TriggerReason = "定时标准化",
        OperationTime = $"2026-01-0{seq % 9 + 1} 09:{seq % 60:00}:00",
        CoeffBefore = coeffBefore,
        CoeffAfter = coeffAfter,
        ResultStatus = AnalysisResultStatus.Success,
    };

    /// <summary>构建 VM 并等待后台加载完成（轮询等待缓存非空）。</summary>
    private static async Task<CoefficientsViewModel> CreateLoadedAsync(
        FakeCoefficientRepository? coeffs = null, FakeOperationLogRepository? logs = null)
    {
        var vm = new CoefficientsViewModel(coeffs ?? new FakeCoefficientRepository(),
            logs ?? new FakeOperationLogRepository(), new FakeUiDispatcher(), new FakeErrorCollector());
        vm.OnNavigatedTo();
        for (var i = 0; i < 100 && vm.CoeffCount == 0 && vm.ChangeCount == 0; i++)
        {
            await Task.Delay(10);
        }

        return vm;
    }

    [Fact]
    public async Task Load_系数进入表格_映射正确()
    {
        var repo = new FakeCoefficientRepository();
        repo.Store.Add(MakeCoeff(element: "Fe"));

        var vm = await CreateLoadedAsync(coeffs: repo);

        Assert.Equal(1, vm.CoeffCount);
        var row = vm.CoefficientRows.Single();
        Assert.Equal("Fe", row.ElementText);
        Assert.Equal("GB/T 7999-A", row.MethodIdText);
        Assert.Equal("v125", row.VersionText);
        Assert.Equal("SUS-001", row.SourceSampleIdText);
        Assert.True(row.IsCurrent);
    }

    [Fact]
    public async Task Load_无来源标样显示占位()
    {
        var repo = new FakeCoefficientRepository();
        repo.Store.Add(MakeCoeff(source: null));

        var vm = await CreateLoadedAsync(coeffs: repo);

        Assert.Equal("—", vm.CoefficientRows.Single().SourceSampleIdText);
    }

    [Fact]
    public async Task 分页_超过每页10条分页浏览()
    {
        var repo = new FakeCoefficientRepository();
        for (var v = 1; v <= 13; v++)
        {
            repo.Store.Add(MakeCoeff(version: v, isCurrent: false, time: $"2026-01-{v:00} 08:00:00"));
        }

        var vm = await CreateLoadedAsync(coeffs: repo);

        Assert.Equal(13, vm.CoeffCount);
        Assert.Equal(2, vm.CoeffTotalPages);
        Assert.Equal(10, vm.CoefficientRows.Count);
        Assert.True(vm.HasCoeffMultiPage);
        Assert.True(vm.HasCoeffNextPage);

        vm.NextCoeffPageCommand.Execute(null);
        Assert.Equal(2, vm.CoeffPage);
        Assert.Equal(3, vm.CoefficientRows.Count);          // 末页剩余 3 条
        Assert.True(vm.HasCoeffPrevPage);
        Assert.False(vm.HasCoeffNextPage);
    }

    [Fact]
    public async Task 筛选_按方法与元素过滤并回到首页()
    {
        var repo = new FakeCoefficientRepository();
        repo.Store.Add(MakeCoeff(method: "GB/T 7999-A", element: "Si"));
        repo.Store.Add(MakeCoeff(method: "GB/T 7999-B", element: "Si"));
        repo.Store.Add(MakeCoeff(method: "GB/T 7999-A", element: "Fe"));

        var vm = await CreateLoadedAsync(coeffs: repo);
        Assert.Equal(3, vm.CoeffCount);

        vm.FilterMethod = "GB/T 7999-B";
        Assert.Equal(1, vm.CoeffCount);
        Assert.Equal("GB/T 7999-B", vm.CoefficientRows.Single().MethodIdText);
        Assert.Equal(1, vm.CoeffPage);                      // 筛选变化回第 1 页

        vm.FilterMethod = "全部";
        vm.FilterElement = "Fe";
        Assert.Equal(1, vm.CoeffCount);
        Assert.Equal("Fe", vm.CoefficientRows.Single().ElementText);
    }

    [Fact]
    public async Task 筛选_仅当前有效版本()
    {
        var repo = new FakeCoefficientRepository();
        repo.Store.Add(MakeCoeff(version: 125, isCurrent: true));
        repo.Store.Add(MakeCoeff(version: 124, isCurrent: false, time: "2025-12-01 08:00:00"));

        var vm = await CreateLoadedAsync(coeffs: repo);
        Assert.Equal(2, vm.CoeffCount);

        vm.FilterCurrentOnly = true;
        Assert.Equal(1, vm.CoeffCount);
        Assert.True(vm.CoefficientRows.Single().IsCurrent);
    }

    [Fact]
    public async Task 变更历史_仅显示带系数前后值的日志()
    {
        var logs = new FakeOperationLogRepository();
        logs.Store.Add(MakeLog(1, coeffBefore: "a=1.2,b=0.1", coeffAfter: "a=1.3,b=0.1"));
        logs.Store.Add(MakeLog(2));                         // 无系数字段，不计入

        var vm = await CreateLoadedAsync(logs: logs);

        Assert.Equal(1, vm.ChangeCount);
        var row = vm.ChangeRows.Single();
        Assert.Equal("SUS-001", row.SampleIdText);
        Assert.Equal("a=1.2,b=0.1", row.CoeffBeforeText);
        Assert.Equal("a=1.3,b=0.1", row.CoeffAfterText);
        Assert.Contains("成功", row.ResultStatusText);
    }

    [Fact]
    public void Tab切换_命令生效()
    {
        var vm = new CoefficientsViewModel(new FakeCoefficientRepository(), new FakeOperationLogRepository(),
            new FakeUiDispatcher(), new FakeErrorCollector());

        Assert.Equal("Current", vm.ActiveTab);
        vm.SetTabCommand.Execute("History");
        Assert.Equal("History", vm.ActiveTab);
        vm.SetTabCommand.Execute("Matrix");
        Assert.Equal("Matrix", vm.ActiveTab);
    }

    [Fact]
    public async Task 页码跳转_越界收敛到末页()
    {
        var repo = new FakeCoefficientRepository();
        for (var v = 1; v <= 25; v++)
        {
            repo.Store.Add(MakeCoeff(version: v, isCurrent: v == 25, time: $"2026-01-{v:00} 08:00:00"));
        }

        var vm = await CreateLoadedAsync(coeffs: repo);

        vm.CoeffPageJumpInput = "999";
        vm.JumpToCoeffPageCommand.Execute(null);
        Assert.Equal(3, vm.CoeffPage);                      // 25 条 → 3 页，999 收敛到末页
        Assert.Equal(5, vm.CoefficientRows.Count);
    }
}
