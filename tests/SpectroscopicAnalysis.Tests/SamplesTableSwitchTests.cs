using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>样品档案「切换数据表」测试：切表加载、行映射、子表删除、样品表控件显隐。</summary>
public sealed class SamplesTableSwitchTests
{
    private static SamplesViewModel Create(
        FakeSampleRepository? repo = null,
        FakeCertifiedValueRepository? certs = null,
        FakeControlLimitRepository? limits = null,
        FakeCoefficientRepository? coeffs = null,
        FakeOperationLogRepository? logs = null,
        FakeDialogService? dialogs = null)
    {
        return new SamplesViewModel(
            repo ?? new FakeSampleRepository(),
            certs ?? new FakeCertifiedValueRepository(),
            limits ?? new FakeControlLimitRepository(),
            coeffs ?? new FakeCoefficientRepository(),
            logs ?? new FakeOperationLogRepository(),
            new FakeUiDispatcher(), dialogs ?? new FakeDialogService(), new FakeErrorCollector());
    }

    private static async Task<SamplesViewModel> CreateWithSubTables(
        FakeCertifiedValueRepository certs,
        FakeControlLimitRepository limits,
        FakeDialogService? dialogs = null)
    {
        var vm = Create(certs: certs, limits: limits, dialogs: dialogs);
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableCertifiedValue);
        return vm;
    }

    [Fact]
    public async Task SetTable_标准值表_加载并映射()
    {
        var certs = new FakeCertifiedValueRepository();
        certs.Store.Add(new SctCertifiedValue { Id = 1, SampleId = "SUS-001", Element = "Si", CertifiedValue = 0.6, Unit = "%", Uncertainty = 0.05 });
        certs.Store.Add(new SctCertifiedValue { Id = 2, SampleId = "SUS-001", Element = "Fe", CertifiedValue = 0.3, Unit = "%" });

        var vm = Create(certs: certs);
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableCertifiedValue);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(SamplesViewModel.TableCertifiedValue, vm.ActiveTable);
        Assert.False(vm.IsSampleTable);                          // 非样品表：录入/筛选控件隐藏
        Assert.Equal("标准值（SCT_CertifiedValue）", vm.ActiveTableDisplay);
        Assert.Equal(2, vm.CertifiedValues.Count);
        Assert.Equal("0.6", vm.CertifiedValues[0].ValueText);
        Assert.Equal("0.05", vm.CertifiedValues[0].UncertaintyText);
        Assert.Equal("—", vm.CertifiedValues[1].UncertaintyText); // 可空显示占位
    }

    [Fact]
    public async Task SetTable_控制限表_加载并映射()
    {
        var limits = new FakeControlLimitRepository();
        limits.Store.Add(new ControlLimit { LimitId = 1, SampleId = "CTL-002", Element = "Si", UpperLimit = 1.2, LowerLimit = 0.8, LimitType = "Sigma" });

        var vm = Create(limits: limits);
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableControlLimit);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Single(vm.ControlLimitRows);
        Assert.Equal("σ 偏差", vm.ControlLimitRows[0].LimitTypeText);
        Assert.Equal("1.2", vm.ControlLimitRows[0].UpperText);
    }

    [Fact]
    public async Task SetTable_系数与日志表_加载并映射()
    {
        var coeffs = new FakeCoefficientRepository();
        coeffs.Store.Add(new CorrectionCoefficient { CoeffId = 1, MethodId = "GB/T 7999", Element = "Si", Slope = 1.5, Intercept = -0.2, EffectiveTime = "2026-11-01 08:00:00", IsCurrent = true, Version = 2 });
        var logs = new FakeOperationLogRepository();
        logs.Store.Add(new SctOperationLog { LogId = 9, SampleId = "CTL-002", OperationType = "ControlCheck", TriggerReason = "定时插入", OperationTime = "2026-11-10 09:00:00", ResultStatus = "Warning" });

        var vm = Create(coeffs: coeffs, logs: logs);
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableCoefficient);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Single(vm.CoefficientRows);
        Assert.Equal("✓ 当前有效", vm.CoefficientRows[0].CurrentText);
        Assert.Equal("v2", vm.CoefficientRows[0].VersionText);
        Assert.Equal("—", vm.CoefficientRows[0].SourceSampleText); // 可空来源显示占位

        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableOperationLog);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Single(vm.OperationLogRows);
        Assert.Equal("控制检查", vm.OperationLogRows[0].OperationTypeText);
        Assert.Equal("▲ 预警", vm.OperationLogRows[0].ResultStatusText);
    }

    [Fact]
    public async Task DeleteCertifiedValue_确认后删除并刷新()
    {
        var certs = new FakeCertifiedValueRepository();
        certs.Store.Add(new SctCertifiedValue { Id = 1, SampleId = "SUS-001", Element = "Si", CertifiedValue = 0.6, Unit = "%" });
        var vm = await CreateWithSubTables(certs, new FakeControlLimitRepository());
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.DeleteCertifiedValueCommand.ExecuteAsync(vm.CertifiedValues[0]);

        Assert.Equal(new long[] { 1 }, certs.Deleted);                          // 主键正确传入仓储
        Assert.Empty(vm.CertifiedValues);                         // 删除后表格刷新
        Assert.Null(vm.DeleteError);
    }

    [Fact]
    public async Task DeleteControlLimit_确认后删除()
    {
        var limits = new FakeControlLimitRepository();
        limits.Store.Add(new ControlLimit { LimitId = 7, SampleId = "CTL-002", Element = "Si", UpperLimit = 1.2, LowerLimit = 0.8, LimitType = "Sigma" });
        var vm = Create(limits: limits);
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableControlLimit);
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.DeleteControlLimitCommand.ExecuteAsync(vm.ControlLimitRows[0]);

        Assert.Equal(new long[] { 7 }, limits.Deleted);
        Assert.Empty(vm.ControlLimitRows);
        Assert.Null(vm.DeleteError);
    }

    [Fact]
    public async Task Delete_弹窗取消则不删除()
    {
        var certs = new FakeCertifiedValueRepository();
        certs.Store.Add(new SctCertifiedValue { Id = 1, SampleId = "SUS-001", Element = "Si", CertifiedValue = 0.6, Unit = "%" });
        var vm = await CreateWithSubTables(certs, new FakeControlLimitRepository(), dialogs: new FakeDialogService { Result = false });
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.DeleteCertifiedValueCommand.ExecuteAsync(vm.CertifiedValues[0]);

        Assert.Empty(certs.Deleted);                              // 未调用仓储删除
        Assert.Single(vm.CertifiedValues);                        // 行保持原样
    }

    [Fact]
    public async Task SetTable_切回样品表_控件恢复且台账刷新()
    {
        var repo = new FakeSampleRepository();
        repo.Store.Add(new SctSample
        {
            SampleId = "SUS-001", SampleName = "标样1", SampleType = SampleType.SettingUp,
            InitialThickness = 12, CurrentThickness = 12, MinThickness = 6, MaxUsageCount = 50, Status = SampleStatus.Normal,
        });
        var vm = Create(repo: repo);
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableCertifiedValue);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.False(vm.IsSampleTable);

        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableSample);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.True(vm.IsSampleTable);                            // 录入/筛选控件重新可见
        Assert.Single(vm.SctSamples);                             // 台账重新加载
    }

    [Fact]
    public async Task SetTable_非法键与重复键_忽略且不报错()
    {
        var vm = Create();

        await vm.SetTableCommand.ExecuteAsync("NotATable");                  // 非法键：忽略
        Assert.Equal(SamplesViewModel.TableSample, vm.ActiveTable);

        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableSample); // 重复键：忽略
        Assert.Equal(SamplesViewModel.TableSample, vm.ActiveTable);
    }
}
