using System.IO;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.SampleManager;
using SpectroscopicAnalysis.Host.SctStrategy;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>
/// SCT 管理 ViewModel 测试：概览统计、类型筛选/搜索、测高登记状态流转（PDF 3.2）、
/// 阈值调整/启用禁用（二次确认）、执行记录倒序、策略配置往返。
/// </summary>
public sealed class SctPageTests
{
    private static SctSample MakeSample(
        string id,
        string type = SampleType.SettingUp,
        string status = SampleStatus.Normal,
        double thickness = 10,
        double minThickness = 5,
        bool isActive = true,
        int usage = 0,
        int maxUsage = 50) => new()
    {
        SampleId = id,
        SampleName = $"标样 {id}",
        SampleType = type,
        AlloyType = "1xxx",
        RackPosition = $"A-{id}",
        CurrentThickness = thickness,
        InitialThickness = 12,
        MinThickness = minThickness,
        UsageCount = usage,
        MaxUsageCount = maxUsage,
        Status = status,
        IsActive = isActive,
    };

    private static (SctViewModel Vm, FakeSampleRepository Repo, FakeOperationLogRepository Logs, FakeSctPolicyStore Policy, FakeDialogService Dialogs) Create(
        IEnumerable<SctSample>? samples = null,
        IEnumerable<SctOperationLog>? logs = null)
    {
        var repo = new FakeSampleRepository();
        foreach (var s in samples ?? [])
        {
            repo.Store.Add(s);
        }

        var logRepo = new FakeOperationLogRepository();
        foreach (var l in logs ?? [])
        {
            logRepo.Store.Add(l);
        }

        var policy = new FakeSctPolicyStore();
        var dialogs = new FakeDialogService();
        var vm = new SctViewModel(repo, logRepo, policy, new FakeUiDispatcher(), dialogs, new FakeErrorCollector());
        return (vm, repo, logRepo, policy, dialogs);
    }

    [Fact]
    public async Task Load_概览统计与最近操作摘要()
    {
        var logs = new[]
        {
            new SctOperationLog { SampleId = "SUS-001", OperationType = "Standardization", OperationTime = "2026-01-01 08:00:00", ResultStatus = "Success" },
            new SctOperationLog { SampleId = "CTL-001", OperationType = "ControlCheck", OperationTime = "2026-01-02 09:00:00", ResultStatus = "Warning" },
        };
        var (vm, repo, _, _, _) = Create(new[]
        {
            MakeSample("SUS-001"),
            MakeSample("CTL-001", SampleType.Control),
            MakeSample("TYS-001", SampleType.TypeStd),
            MakeSample("SUS-002", status: SampleStatus.NeedMeasure),
            MakeSample("SUS-003", status: SampleStatus.Disabled),
            MakeSample("PRD-001", SampleType.Production), // 生产样不计入
        }, logs);

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.SettingUpCount);      // SUS-001/002/003
        Assert.Equal(1, vm.ControlCount);
        Assert.Equal(1, vm.TypeStdCount);
        Assert.Equal(2, vm.NeedMaintenanceCount); // NeedMeasure + Disabled
        Assert.Contains("CTL-001", vm.LastControlCheckSummary);
        Assert.Contains("▲ 预警", vm.LastControlCheckSummary);
        Assert.Contains("SUS-001", vm.LastStandardizationSummary);
        Assert.Equal(5, vm.SctSamples.Count);    // 生产样被过滤
    }

    [Fact]
    public async Task Filter_类型筛选与关键词搜索()
    {
        var (vm, _, _, _, _) = Create(new[]
        {
            MakeSample("SUS-001"),
            MakeSample("CTL-001", SampleType.Control, thickness: 8),
            MakeSample("SUS-002"),
        });
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SetFilterCommand.Execute(SampleType.Control);
        Assert.Single(vm.SctSamples);
        Assert.Equal("CTL-001", vm.SctSamples[0].SampleId);

        vm.SetFilterCommand.Execute(SctViewModel.FilterAll);
        Assert.Equal(3, vm.SctSamples.Count);

        vm.SearchText = "CTL";
        vm.SearchCommand.Execute(null);
        Assert.Single(vm.SctSamples);
        Assert.Equal("CTL-001", vm.SctSamples[0].SampleId);
    }

    [Fact]
    public async Task Measure_厚度合格_恢复Normal并写审计日志()
    {
        var (vm, repo, logs, _, _) = Create(new[]
        {
            MakeSample("SUS-001", status: SampleStatus.NeedMeasure, thickness: 9, minThickness: 5),
        });
        await vm.LoadCommand.ExecuteAsync(null);
        vm.SelectedSample = vm.SctSamples[0];

        vm.OpenMeasureCommand.Execute(null);
        vm.MeasureThicknessText = "9.5";
        await vm.ApplyMeasureCommand.ExecuteAsync(null);

        var sample = repo.GetById("SUS-001")!;
        Assert.Equal(SampleStatus.Normal, sample.Status);       // 厚度合格 → Normal
        Assert.Equal(9.5, sample.CurrentThickness);
        Assert.NotNull(sample.LastHeightCheckTime);
        Assert.Single(logs.Inserted);
        Assert.Equal("HeightMeasure", logs.Inserted[0].OperationType);
        Assert.Equal(9, logs.Inserted[0].ThicknessBefore);
        Assert.Equal(9.5, logs.Inserted[0].ThicknessAfter);
    }

    [Fact]
    public async Task Measure_完成后发布样品数据变更通知()
    {
        var repo = new FakeSampleRepository();
        repo.Store.Add(MakeSample("SUS-001", status: SampleStatus.NeedMeasure, thickness: 9, minThickness: 5));
        var notifier = new SampleDataChangeNotifier();
        var notified = 0;
        notifier.SampleDataChanged += () => notified++;
        var vm = new SctViewModel(repo, new FakeOperationLogRepository(), new FakeSctPolicyStore(),
            new FakeUiDispatcher(), new FakeDialogService(), new FakeErrorCollector(),
            strategyEngine: null, sampleDataChanged: notifier);
        await vm.LoadCommand.ExecuteAsync(null);
        vm.SelectedSample = vm.SctSamples[0];

        vm.OpenMeasureCommand.Execute(null);
        vm.MeasureThicknessText = "9.5";
        await vm.ApplyMeasureCommand.ExecuteAsync(null);

        Assert.Equal(1, notified);   // 维护写库后通知样品档案等台账页面重载
        Assert.False(vm.IsMeasureOpen);
    }

    [Fact]
    public async Task Measure_低于最小厚度_标记Disabled()
    {
        var (vm, repo, logs, _, _) = Create(new[]
        {
            MakeSample("SUS-001", thickness: 9, minThickness: 5),
        });
        await vm.LoadCommand.ExecuteAsync(null);
        vm.SelectedSample = vm.SctSamples[0];

        vm.OpenMeasureCommand.Execute(null);
        vm.MeasureThicknessText = "4.2";
        await vm.ApplyMeasureCommand.ExecuteAsync(null);

        Assert.Equal(SampleStatus.Disabled, repo.GetById("SUS-001")!.Status);
        Assert.Single(logs.Inserted);
    }

    [Fact]
    public async Task Measure_非法厚度_校验失败不写库()
    {
        var (vm, repo, logs, _, _) = Create(new[] { MakeSample("SUS-001") });
        await vm.LoadCommand.ExecuteAsync(null);
        vm.SelectedSample = vm.SctSamples[0];
        vm.OpenMeasureCommand.Execute(null);

        vm.MeasureThicknessText = "abc";
        await vm.ApplyMeasureCommand.ExecuteAsync(null);
        Assert.NotNull(vm.MeasureError);
        Assert.Empty(logs.Inserted);
        Assert.Empty(repo.Updated);

        vm.MeasureThicknessText = "-1";
        await vm.ApplyMeasureCommand.ExecuteAsync(null);
        Assert.NotNull(vm.MeasureError);
        Assert.Empty(logs.Inserted);
    }

    [Fact]
    public async Task Threshold_调整成功需二次确认_且低于新阈值立即Disabled()
    {
        var (vm, repo, _, _, dialogs) = Create(new[]
        {
            MakeSample("SUS-001", thickness: 8, minThickness: 5),
        });
        await vm.LoadCommand.ExecuteAsync(null);
        vm.SelectedSample = vm.SctSamples[0];

        vm.OpenThresholdCommand.Execute(null);
        Assert.Equal("5.0", vm.EditMinThicknessText);           // 预填当前值
        vm.EditMinThicknessText = "10";                          // 新阈值高于当前厚度
        vm.EditMaxUsageText = "80";
        await vm.SaveThresholdsCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Messages);                        // 二次确认弹窗
        var sample = repo.GetById("SUS-001")!;
        Assert.Equal(10, sample.MinThickness);
        Assert.Equal(80, sample.MaxUsageCount);
        Assert.Equal(SampleStatus.Disabled, sample.Status);     // 低于新阈值立即标记
        Assert.Single(repo.Updated);
    }

    [Fact]
    public async Task Threshold_用户取消确认_不写库()
    {
        var (vm, repo, _, _, dialogs) = Create(new[] { MakeSample("SUS-001") });
        await vm.LoadCommand.ExecuteAsync(null);
        vm.SelectedSample = vm.SctSamples[0];
        dialogs.Result = false;

        vm.OpenThresholdCommand.Execute(null);
        vm.EditMinThicknessText = "9";
        await vm.SaveThresholdsCommand.ExecuteAsync(null);

        Assert.Empty(repo.Updated);
        Assert.Equal(5, repo.GetById("SUS-001")!.MinThickness);
    }

    [Fact]
    public async Task ToggleActive_禁用与启用均需确认并写库()
    {
        var (vm, repo, _, _, dialogs) = Create(new[] { MakeSample("SUS-001", isActive: true) });
        await vm.LoadCommand.ExecuteAsync(null);
        vm.SelectedSample = vm.SctSamples[0];

        await vm.ToggleActiveCommand.ExecuteAsync(null);
        Assert.False(repo.GetById("SUS-001")!.IsActive);
        Assert.Contains("禁用", dialogs.Messages[0]);

        await vm.LoadCommand.ExecuteAsync(null);
        vm.SelectedSample = vm.SctSamples[0];
        await vm.ToggleActiveCommand.ExecuteAsync(null);
        Assert.True(repo.GetById("SUS-001")!.IsActive);
    }

    [Fact]
    public async Task Logs_执行记录倒序加载()
    {
        var logs = new[]
        {
            new SctOperationLog { SampleId = "SUS-001", OperationType = "Standardization", OperationTime = "2026-01-01 08:00:00", ResultStatus = "Success" },
            new SctOperationLog { SampleId = "SUS-002", OperationType = "HeightMeasure", OperationTime = "2026-01-03 10:00:00", ResultStatus = "Success", ThicknessBefore = 9, ThicknessAfter = 8.5 },
        };
        var (vm, _, _, _, _) = Create(new[] { MakeSample("SUS-001") }, logs);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.OperationRows.Count);
        Assert.Equal("SUS-002", vm.OperationRows[0].SampleIdText); // 时间倒序
        Assert.Contains("9.0 → 8.5", vm.OperationRows[0].ThicknessText);
    }

    [Fact]
    public void Policy_保存成功写入全流程日志含变更前后值()
    {
        var systemLog = new SystemLogCollector();
        var policy = new FakeSctPolicyStore();
        var vm = new SctViewModel(new FakeSampleRepository(), new FakeOperationLogRepository(), policy,
            new FakeUiDispatcher(), new FakeDialogService(), new FakeErrorCollector(),
            strategyEngine: null, sampleDataChanged: null, systemLog: systemLog);

        // 修改插入模式与间隔后保存 → 日志含类目与旧→新对照
        vm.PolicyInsertMode = ControlInsertMode.ByTime;
        vm.PolicyInsertMinutes = "45";
        vm.SavePolicyCommand.Execute(null);

        Assert.Null(vm.PolicyError);
        var entry = Assert.Single(systemLog.GetEntries());
        Assert.Equal("SCT策略", entry.Category);
        Assert.Contains("策略配置已保存", entry.Message);
        Assert.Contains("插入模式 按数量→按时间", entry.Message);
        Assert.Contains("插入间隔 60→45 分钟", entry.Message);
    }

    [Fact]
    public void Policy_内容无变化保存_不写全流程日志()
    {
        var systemLog = new SystemLogCollector();
        var policy = new FakeSctPolicyStore();
        var vm = new SctViewModel(new FakeSampleRepository(), new FakeOperationLogRepository(), policy,
            new FakeUiDispatcher(), new FakeDialogService(), new FakeErrorCollector(),
            strategyEngine: null, sampleDataChanged: null, systemLog: systemLog);

        // 加载当前配置后不做任何修改直接保存：配置写入成功但不写全流程日志
        vm.LoadCommand.Execute(null);
        vm.SavePolicyCommand.Execute(null);

        Assert.Null(vm.PolicyError);
        Assert.NotNull(policy.LastSaved);
        Assert.Empty(systemLog.GetEntries());
    }

    [Fact]
    public void Policy_保存校验与往返()
    {
        var (vm, _, _, policy, _) = Create();

        vm.PolicyInsertCount = "0";
        vm.SavePolicyCommand.Execute(null);
        Assert.NotNull(vm.PolicyError);                          // 非法值被拦截
        Assert.Null(policy.LastSaved);

        vm.PolicySettingSelectionMode = SettingSampleSelectionMode.RoundRobin;
        vm.PolicyRotationEnabled = false;
        vm.PolicyTypeOnAlloySwitch = false;
        vm.PolicyTypePeriodEnabled = true;
        vm.PolicyTypePeriodDays = "0";
        vm.PolicyRecheckMax = "6";
        vm.PolicyInsertMode = ControlInsertMode.ByTime;
        vm.PolicyInsertCount = "12";
        vm.PolicyInsertMinutes = "45";
        vm.PolicyLimitType = "Percent";
        vm.PolicyLimitValue = "5";
        vm.AutoOnControlFail = false;
        vm.PeriodicEnabled = true;
        vm.PeriodicHours = "4";

        // 新增字段非法值被拦截（期限天 0 / 重测次数超上限）
        vm.SavePolicyCommand.Execute(null);
        Assert.NotNull(vm.PolicyError);
        Assert.Null(policy.LastSaved);
        vm.PolicyTypePeriodDays = "14";
        vm.SavePolicyCommand.Execute(null);
        Assert.NotNull(vm.PolicyError);
        Assert.Null(policy.LastSaved);

        vm.PolicyRecheckMax = "2";
        vm.SavePolicyCommand.Execute(null);

        Assert.Null(vm.PolicyError);
        Assert.NotNull(policy.LastSaved);
        Assert.Equal(SettingSampleSelectionMode.RoundRobin, policy.LastSaved!.SettingSampleSelectionMode);
        Assert.False(policy.LastSaved.ControlRotationEnabled);
        Assert.False(policy.LastSaved.TypeStandardizeOnAlloySwitch);
        Assert.True(policy.LastSaved.TypeStandardizePeriodEnabled);
        Assert.Equal(14, policy.LastSaved.TypeStandardizePeriodDays);
        Assert.Equal(2, policy.LastSaved.ControlRecheckMaxCount);
        Assert.Equal(ControlInsertMode.ByTime, policy.LastSaved.ControlInsertMode);
        Assert.Equal(12, policy.LastSaved.ControlInsertCount);
        Assert.Equal(45, policy.LastSaved.ControlInsertMinutes);
        Assert.Equal("Percent", policy.LastSaved.LimitType);
        Assert.Equal(5, policy.LastSaved.LimitValue);
        Assert.False(policy.LastSaved.AutoStandardizeOnControlFail);
        Assert.True(policy.LastSaved.PeriodicStandardizeEnabled);
        Assert.Equal(4, policy.LastSaved.PeriodicStandardizeHours);

        // 往返：新 VM 从同一 store 加载
        var vm2 = new SctViewModel(new FakeSampleRepository(), new FakeOperationLogRepository(), policy,
            new FakeUiDispatcher(), new FakeDialogService(), new FakeErrorCollector());
        Assert.Equal(SettingSampleSelectionMode.RoundRobin, vm2.PolicySettingSelectionMode);
        Assert.False(vm2.PolicyRotationEnabled);
        Assert.True(vm2.PolicyTypePeriodEnabled);
        Assert.Equal("14", vm2.PolicyTypePeriodDays);
        Assert.Equal("2", vm2.PolicyRecheckMax);
        Assert.Equal("12", vm2.PolicyInsertCount);
        Assert.Equal("Percent", vm2.PolicyLimitType);
        Assert.False(vm2.AutoOnControlFail);
    }

    [Fact]
    public void PolicyStore_旧版JSON缺新字段回退默认值()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sct_policy_test_{Guid.NewGuid():N}.json");
        try
        {
            // 旧版配置（仅含第一版字段）
            File.WriteAllText(path, """{ "ControlInsertMode": "ByTime", "ControlInsertMinutes": 30 }""");
            var store = new JsonSctPolicyStore(path);

            var loaded = store.Load();

            Assert.Equal("ByTime", loaded.ControlInsertMode);          // 旧字段正常读取
            Assert.Equal(30, loaded.ControlInsertMinutes);
            Assert.Equal("LeastUsed", loaded.SettingSampleSelectionMode); // 新字段回退默认值
            Assert.True(loaded.ControlRotationEnabled);
            Assert.Equal(1, loaded.ControlRecheckMaxCount);

            // 往返：保存后新字段落盘
            loaded.TypeStandardizePeriodDays = 9;
            store.Save(loaded);
            Assert.Equal(9, new JsonSctPolicyStore(path).Load().TypeStandardizePeriodDays);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
