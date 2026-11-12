using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>样品档案「切换数据表」测试：切表加载、行映射、子表删除、样品表控件显隐。</summary>
public sealed class SamplesTableSwitchTests
{
    private static SamplesViewModel Create(
        FakeSampleRepository? repo = null,
        FakeProductionSampleRepository? production = null,
        FakeCertifiedValueRepository? certs = null,
        FakeControlLimitRepository? limits = null,
        FakeCoefficientRepository? coeffs = null,
        FakeOperationLogRepository? logs = null,
        FakeDialogService? dialogs = null)
    {
        return new SamplesViewModel(
            repo ?? new FakeSampleRepository(),
            production ?? new FakeProductionSampleRepository(),
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
    public async Task SetTable_生产样与分析结果表_加载映射且专属搜索生效()
    {
        var production = new FakeProductionSampleRepository();
        production.Store.Add(new ProductionSample { SampleId = "PRD-G001", SampleName = "生产样 G-001", AlloyType = "1xxx", RackPosition = "D11", AssociatedMethod = "GB/T 7999-A", Status = ProductionStatus.Completed, CreatedTime = DateTime.Today, Remark = "常规生产分析" });
        production.Store.Add(new ProductionSample { SampleId = "PRD-G002", SampleName = "生产样 G-002", AlloyType = "6xxx", Status = ProductionStatus.Analyzing, CreatedTime = DateTime.Today });
        production.InsertResult(new AnalysisResult { SampleId = "PRD-G001", MeasuredConc = 0.12, CorrectedConc = 0.119, CoeffVersion = 7, IsPass = true, ResultStatus = AnalysisResultStatus.Success, ArchivePosition = "A-01-1", OperationTime = DateTime.Today });

        var vm = Create(production: production);

        // 生产样档案表
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableProductionSample);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("生产样档案（Production_Sample）", vm.ActiveTableDisplay);
        Assert.Equal(2, vm.ProductionRows.Count);
        Assert.Equal("✓ 已完成", vm.ProductionRows[0].StatusText);

        vm.ProductionSearchText = "G002";
        vm.SearchCommand.Execute(null);
        Assert.Single(vm.ProductionRows);                        // 专属搜索命中
        Assert.Equal("PRD-G002", vm.ProductionRows[0].SampleId);
        vm.ProductionSearchText = string.Empty;
        vm.SearchCommand.Execute(null);

        // 分析结果表
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableAnalysisResult);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("分析结果（Analysis_Result）", vm.ActiveTableDisplay);
        Assert.Single(vm.AnalysisResultRows);
        Assert.Equal("v7", vm.AnalysisResultRows[0].CoeffVersionText);
        Assert.Equal("✓ 合格", vm.AnalysisResultRows[0].PassText);
        Assert.Equal("A-01-1", vm.AnalysisResultRows[0].ArchiveText);

        vm.AnalysisSearchText = "不存在的关键字";
        vm.SearchCommand.Execute(null);
        Assert.Empty(vm.AnalysisResultRows);                     // 专属搜索不命中
    }

    [Fact]
    public async Task SearchText_切表时关键词按表独立记忆()
    {
        var production = new FakeProductionSampleRepository();
        production.Store.Add(new ProductionSample { SampleId = "PRD-G001", SampleName = "生产样 G-001", AlloyType = "1xxx", Status = ProductionStatus.Completed, CreatedTime = DateTime.Today });
        production.Store.Add(new ProductionSample { SampleId = "PRD-G002", SampleName = "生产样 G-002", AlloyType = "6xxx", Status = ProductionStatus.Analyzing, CreatedTime = DateTime.Today });

        var vm = Create(production: production);

        // 台账表输入关键词并搜索
        vm.SearchText = "keepA";
        vm.SearchCommand.Execute(null);

        // 切到生产样档案表：搜索框切换为该表缓存的关键词（初始为空），数据不过滤
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableProductionSample);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Equal(2, vm.ProductionRows.Count);

        // 生产样表输入自己的关键词并搜索
        vm.SearchText = "G002";
        vm.SearchCommand.Execute(null);
        Assert.Single(vm.ProductionRows);
        Assert.Equal("PRD-G002", vm.ProductionRows[0].SampleId);

        // 切回台账表：关键词恢复为台账表上次输入的值
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableSample);
        Assert.Equal("keepA", vm.SearchText);
    }

    [Fact]
    public void SearchHint_随激活表切换提示词()
    {
        var vm = Create();
        Assert.Contains("关键字", vm.SearchHint);                // 默认台账表

        vm.ActiveTable = SamplesViewModel.TableProductionSample;
        Assert.Contains("生产样", vm.SearchHint);
        Assert.False(vm.HasSearchText);

        vm.SearchText = "abc";
        Assert.True(vm.HasSearchText);                          // 输入关键词后水印隐藏

        vm.ActiveTable = SamplesViewModel.TableAnalysisResult;
        Assert.Contains("分析结果", vm.SearchHint);
        Assert.False(vm.HasSearchText);                         // 切表后按新表缓存判定
    }

    [Fact]
    public async Task SearchText_专属属性与路由属性指向同一表缓存()
    {
        var vm = Create();

        vm.ProductionSearchText = "prod-key";
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableProductionSample);
        Assert.Equal("prod-key", vm.SearchText);            // 路由属性读到生产样表缓存

        vm.SearchText = "route-key";
        Assert.Equal("route-key", vm.ProductionSearchText); // 专属属性同步更新

        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableAnalysisResult);
        vm.AnalysisSearchText = "analysis-key";
        Assert.Equal("analysis-key", vm.SearchText);
        Assert.Equal("route-key", vm.ProductionSearchText); // 生产样表缓存未被分析结果表影响
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
