using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>子表分页/搜索/删除测试（与样品台账同规则：每页 15 条、共用搜索框、删除二次确认）。</summary>
public sealed class SamplesSubTablePagingTests
{
    private static SamplesViewModel Create(
        FakeCertifiedValueRepository? certs = null,
        FakeControlLimitRepository? limits = null,
        FakeCoefficientRepository? coeffs = null,
        FakeOperationLogRepository? logs = null,
        FakeDialogService? dialogs = null,
        FakeSampleRepository? samples = null)
    {
        return new SamplesViewModel(
            samples ?? new FakeSampleRepository(),
            new FakeProductionSampleRepository(),
            certs ?? new FakeCertifiedValueRepository(),
            limits ?? new FakeControlLimitRepository(),
            coeffs ?? new FakeCoefficientRepository(),
            logs ?? new FakeOperationLogRepository(),
            new FakeSctStrategyEngine(),
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
        Assert.Equal(3, vm.TotalPages);
        Assert.Equal(15, vm.CertifiedValues.Count);             // 第 1 页满 15 条
        Assert.Equal("S-001", vm.CertifiedValues[0].SampleId);

        vm.NextPageCommand.Execute(null);
        Assert.Equal("S-016", vm.CertifiedValues[0].SampleId);  // 第 2 页从 S-016 起

        vm.NextPageCommand.Execute(null);
        Assert.Equal(15, vm.CertifiedValues.Count);             // 末页 45 - 30 = 15 条（满页）
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
        Assert.Equal(15, vm.CertifiedValues.Count);             // 15 条不超过页大小，全在本页
        Assert.Equal(1, vm.TotalPages);
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
        Assert.Equal(15, vm.CertifiedValues.Count);
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

    // ==================== 详情面板轨迹日志分页（10 条/页） ====================

    private static SctOperationLog MakePagedLog(long id, string sid, DateTime time) => new()
    { LogId = id, SampleId = sid, OperationType = "ControlCheck", TriggerReason = "定时插入", ResultStatus = "Success", OperationTime = time.ToString("yyyy-MM-dd HH:mm:ss") };

    /// <summary>选中样品行并等待详情子表加载完成（构造期 _activeTableLoad 与详情查询均为后台任务，轮询等待）。</summary>
    private static async Task SelectSampleAndWaitAsync(SamplesViewModel vm, string sampleId, int expectedLogCount)
    {
        await vm.LoadCommand.ExecuteAsync(null);    // 显式加载台账（默认 ActiveTable 已是样品表时 SetTable 不触发加载）
        for (var i = 0; i < 50 && !vm.SctSamples.Any(r => r.SampleId == sampleId); i++)
        {
            await Task.Delay(10);       // 等台账首次加载完成
        }
        vm.SelectedRow = vm.SctSamples.First(r => r.SampleId == sampleId);
        for (var i = 0; i < 50 && vm.DetailLogTotalCount != expectedLogCount; i++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task 轨迹日志超10条_每页10条分页切换正确()
    {
        var samples = new FakeSampleRepository();
        samples.Store.Add(new SctSample { SampleId = "HC-PG", SampleName = "分页样", SampleType = SampleType.Control, RackPosition = "B01", InitialThickness = 10, CurrentThickness = 10, MinThickness = 5, MaxUsageCount = 60, Status = SampleStatus.Normal });
        var logs = new FakeOperationLogRepository();
        for (var i = 1; i <= 25; i++)
        {
            logs.Store.Add(MakePagedLog(i, "HC-PG", new DateTime(2026, 11, 10).AddHours(-i)));   // 时间倒序注入
        }

        var vm = Create(samples: samples, logs: logs);
        // 回归：总条数为直读缓存的计算属性，缓存替换后必须主动发通知，否则分页脚"共 N 条"停留旧值（如"共 0 条"）
        var totalChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SamplesViewModel.DetailLogTotalCount))
            {
                totalChanged.TrySetResult(true);
            }
        };

        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableSample);
        await SelectSampleAndWaitAsync(vm, "HC-PG", 25);

        Assert.True(await totalChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)), "详情加载后应发出 DetailLogTotalCount 变更通知");

        Assert.Equal(25, vm.DetailLogTotalCount);               // 全量 25 条不再截断最近 10 条
        Assert.Equal(3, vm.DetailLogTotalPages);
        Assert.Equal(10, vm.DetailLogs.Count);                  // 第 1 页满 10 条
        Assert.True(vm.HasNextDetailLogPage);
        Assert.False(vm.HasPrevDetailLogPage);
        Assert.Equal("第 1 / 3 页", vm.DetailLogPageDisplay);

        vm.NextDetailLogPageCommand.Execute(null);
        Assert.Equal(10, vm.DetailLogs.Count);
        Assert.True(vm.HasPrevDetailLogPage);

        vm.NextDetailLogPageCommand.Execute(null);
        Assert.Equal(5, vm.DetailLogs.Count);                   // 末页 25 - 20 = 5 条
        Assert.False(vm.HasNextDetailLogPage);

        vm.FirstDetailLogPageCommand.Execute(null);
        Assert.Equal(1, vm.DetailLogCurrentPage);
        Assert.Equal(10, vm.DetailLogs.Count);
    }

    [Fact]
    public async Task 轨迹日志不超10条_单页无翻页()
    {
        var samples = new FakeSampleRepository();
        samples.Store.Add(new SctSample { SampleId = "HC-ONE", SampleName = "单页样", SampleType = SampleType.Control, RackPosition = "B02", InitialThickness = 10, CurrentThickness = 10, MinThickness = 5, MaxUsageCount = 60, Status = SampleStatus.Normal });
        var logs = new FakeOperationLogRepository();
        for (var i = 1; i <= 8; i++)
        {
            logs.Store.Add(MakePagedLog(i, "HC-ONE", new DateTime(2026, 11, 10).AddHours(-i)));
        }

        var vm = Create(samples: samples, logs: logs);
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableSample);
        await SelectSampleAndWaitAsync(vm, "HC-ONE", 8);

        Assert.Equal(1, vm.DetailLogTotalPages);
        Assert.Equal(8, vm.DetailLogs.Count);
        Assert.False(vm.HasNextDetailLogPage);
        Assert.False(vm.HasPrevDetailLogPage);
    }

    [Fact]
    public async Task 切换选中样_轨迹日志页码重置第1页()
    {
        var samples = new FakeSampleRepository();
        samples.Store.Add(new SctSample { SampleId = "HC-A", SampleName = "多样A", SampleType = SampleType.Control, RackPosition = "B03", InitialThickness = 10, CurrentThickness = 10, MinThickness = 5, MaxUsageCount = 60, Status = SampleStatus.Normal });
        samples.Store.Add(new SctSample { SampleId = "HC-B", SampleName = "多样B", SampleType = SampleType.Control, RackPosition = "B04", InitialThickness = 10, CurrentThickness = 10, MinThickness = 5, MaxUsageCount = 60, Status = SampleStatus.Normal });
        var logs = new FakeOperationLogRepository();
        for (var i = 1; i <= 25; i++)
        {
            logs.Store.Add(MakePagedLog(i, "HC-A", new DateTime(2026, 11, 10).AddHours(-i)));
        }
        logs.Store.Add(MakePagedLog(900, "HC-B", new DateTime(2026, 11, 10).AddHours(-1)));

        var vm = Create(samples: samples, logs: logs);
        await vm.SetTableCommand.ExecuteAsync(SamplesViewModel.TableSample);
        await SelectSampleAndWaitAsync(vm, "HC-A", 25);
        vm.NextDetailLogPageCommand.Execute(null);
        Assert.Equal(2, vm.DetailLogCurrentPage);

        await SelectSampleAndWaitAsync(vm, "HC-B", 1);
        Assert.Equal(1, vm.DetailLogCurrentPage);               // 换样重置第 1 页
        Assert.Single(vm.DetailLogs);
    }
}
