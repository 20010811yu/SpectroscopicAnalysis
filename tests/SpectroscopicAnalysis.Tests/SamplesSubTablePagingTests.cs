using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>子表分页/搜索/删除测试（与样品台账同规则：每页 10 条、共用搜索框、删除二次确认）。</summary>
public sealed class SamplesSubTablePagingTests
{
    private static SamplesViewModel Create(
        FakeCertifiedValueRepository? certs = null,
        FakeControlLimitRepository? limits = null,
        FakeCoefficientRepository? coeffs = null,
        FakeOperationLogRepository? logs = null,
        FakeDialogService? dialogs = null)
    {
        return new SamplesViewModel(
            new FakeSampleRepository(),
            new FakeProductionSampleRepository(),
            certs ?? new FakeCertifiedValueRepository(),
            limits ?? new FakeControlLimitRepository(),
            coeffs ?? new FakeCoefficientRepository(),
            logs ?? new FakeOperationLogRepository(),
            new FakeUiDispatcher(), dialogs ?? new FakeDialogService(), new FakeErrorCollector());
    }

    private static SctCertifiedValue MakeCert(long id, string sid, string element) => new()
    { Id = id, SampleId = sid, Element = element, CertifiedValue = 0.5, Unit = "%" };

    private static CorrectionCoefficient MakeCoeff(long id, string method, string element, int version) => new()
    { CoeffId = id, MethodId = method, Element = element, Slope = 1, Intercept = 0, EffectiveTime = "2026-11-01", Version = version };

    private static SctOperationLog MakeLog(long id, string sid, string opType) => new()
    { LogId = id, SampleId = sid, OperationType = opType, OperationTime = "2026-11-10 08:00:00", ResultStatus = "Success" };

    [Fact]
    public async Task 子表超过20行_分页切片正确()
    {
        var certs = new FakeCertifiedValueRepository();
        for (var i = 1; i <= 45; i++)
        {
            certs.Store.Add(MakeCert(i, $"S-{i:000}", "Si"));
        }

        var vm = Create(certs: certs);
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableCertifiedValue);

        Assert.Equal(45, vm.TotalCount);
        Assert.Equal(5, vm.TotalPages);
        Assert.Equal(10, vm.CertifiedValues.Count);             // 第 1 页满 10 条
        Assert.Equal("S-001", vm.CertifiedValues[0].SampleId);

        vm.NextPageCommand.Execute(null);
        Assert.Equal("S-011", vm.CertifiedValues[0].SampleId);  // 第 2 页从 S-011 起

        vm.NextPageCommand.Execute(null);
        vm.NextPageCommand.Execute(null);
        vm.NextPageCommand.Execute(null);
        Assert.Equal(5, vm.CurrentPage);
        Assert.Equal(5, vm.CertifiedValues.Count);              // 末页 45 - 40 = 5 条
        Assert.False(vm.HasNextPage);
    }

    [Fact]
    public async Task 子表搜索_按元素过滤并回到第1页()
    {
        var certs = new FakeCertifiedValueRepository();
        for (var i = 1; i <= 30; i++)
        {
            certs.Store.Add(MakeCert(i, $"S-{i:000}", i % 2 == 0 ? "Fe" : "Si"));
        }

        var vm = Create(certs: certs);
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableCertifiedValue);

        vm.SearchText = "Fe";
        vm.SearchCommand.Execute(null);

        Assert.Equal(15, vm.TotalCount);                        // 仅 Fe 行
        Assert.All(vm.CertifiedValues, r => Assert.Equal("Fe", r.Element));
        Assert.Equal(SamplesViewModel.PageSize, vm.CertifiedValues.Count); // 15 条 > 页大小 → 第 1 页满页
        Assert.Equal(2, vm.TotalPages);
    }

    [Fact]
    public async Task 切表时页码重置到第1页()
    {
        var certs = new FakeCertifiedValueRepository();
        for (var i = 1; i <= 45; i++)
        {
            certs.Store.Add(MakeCert(i, $"S-{i:000}", "Si"));
        }

        var vm = Create(certs: certs);
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableCertifiedValue);
        vm.NextPageCommand.Execute(null);
        Assert.Equal(2, vm.CurrentPage);

        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableControlLimit);
        Assert.Equal(1, vm.CurrentPage);                        // 切表重置第 1 页

        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableCertifiedValue);
        Assert.Equal(1, vm.CurrentPage);                        // 切回同样重置
        Assert.Equal(10, vm.CertifiedValues.Count);
    }

    [Fact]
    public async Task 删除系数_确认后删除并刷新()
    {
        var coeffs = new FakeCoefficientRepository();
        coeffs.Store.Add(MakeCoeff(11, "GB/T 7999-A", "Si", 3));
        var vm = Create(coeffs: coeffs);
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableCoefficient);

        await vm.DeleteCoefficientCommand.ExecuteAsync(vm.CoefficientRows[0]);

        Assert.Equal(new long[] { 11 }, coeffs.Deleted);
        Assert.Empty(vm.CoefficientRows);
        Assert.Equal(0, vm.TotalCount);
        Assert.Null(vm.DeleteError);
    }

    [Fact]
    public async Task 删除操作日志_确认后删除_取消则保留()
    {
        var logs = new FakeOperationLogRepository();
        logs.Store.Add(MakeLog(9, "CTL-002", "ControlCheck"));
        var dialogs = new FakeDialogService();
        var vm = Create(logs: logs, dialogs: dialogs);
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableOperationLog);

        await vm.DeleteOperationLogCommand.ExecuteAsync(vm.OperationLogRows[0]);
        Assert.Equal(new long[] { 9 }, logs.Deleted);
        Assert.Empty(vm.OperationLogRows);

        // 取消路径
        logs.Store.Add(MakeLog(10, "CTL-003", "HeightMeasure"));
        dialogs.Result = false;
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.DeleteOperationLogCommand.ExecuteAsync(vm.OperationLogRows[0]);
        Assert.Empty(logs.Deleted.Where(d => d == 10));         // 未删除
        Assert.Single(vm.OperationLogRows);                     // 行保持原样
    }
}
