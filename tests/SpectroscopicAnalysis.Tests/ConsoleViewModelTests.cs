using CommunityToolkit.Mvvm.ComponentModel;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.SampleManager;
using SpectroscopicAnalysis.Host.SctStrategy;
using SpectroscopicAnalysis.Host.ViewModels;
using SpectroscopicAnalysis.Host.Workflow;

namespace SpectroscopicAnalysis.Tests;

/// <summary>主控台 ViewModel 测试：报警面板确认、全流程日志映射、货架示意映射（SCT 5×5 + 分析样 5×5）、SCT 当前策略摘要。</summary>
public sealed class ConsoleViewModelTests
{
    private static ConsoleViewModel CreateVm(
        FakeErrorCollector? errors = null,
        FakeSampleRepository? samples = null,
        FakeProductionSampleRepository? production = null,
        FakeSctPolicyStore? policy = null,
        FakeWorkflowOrchestrator? orchestrator = null,
        SampleDataChangeNotifier? notifier = null)
    {
        return new ConsoleViewModel(errors ?? new FakeErrorCollector(), new SystemLogCollector(),
            samples ?? new FakeSampleRepository(), production ?? new FakeProductionSampleRepository(),
            policy ?? new FakeSctPolicyStore(),
            new FakeUiDispatcher(), new LaserMarkingViewModel(new FakeLaserMarkingService(), new MainViewModelTests.FakeEzCad2Controller(), new FakeUiDispatcher()),
            orchestrator ?? new FakeWorkflowOrchestrator(), notifier ?? new SampleDataChangeNotifier());
    }

    // ==================== 当前任务卡（与任务队列执行中任务绑定） ====================

    [Fact]
    public void RefreshCurrentTask_执行中快照_标题含样品编号与任务类型()
    {
        var samples = new FakeSampleRepository();
        samples.Store.Add(new SctSample
        {
            SampleId = "SUS-G675", SampleName = "控样 G675", SampleType = SampleType.SettingUp,
            Status = SampleStatus.Normal, IsActive = true, UsageCount = 0, CreatedTime = DateTime.Now,
        });
        var production = new FakeProductionSampleRepository();
        production.Store.Add(new ProductionSample
        {
            SampleId = "PRD-G2850", SampleName = "铝锭 A", SampleType = "Production",
            AlloyType = "3003", Status = ProductionStatus.Pending, CreatedTime = DateTime.Now,
        });
        var orchestrator = new FakeWorkflowOrchestrator
        {
            Snapshot = new WorkflowSnapshot(12, SctTaskType.Production, "PRD-G2850", TaskState.Analyzing, null, IsRunning: true, new Dictionary<string, bool>()),
        };
        var vm = CreateVm(production: production, samples: samples, orchestrator: orchestrator);

        Assert.Equal("当前任务 PRD-G2850 生产样分析", vm.CurrentTaskTitle);
        Assert.Equal("● 激发分析中", vm.CurrentTaskBadgeText);
        Assert.True(vm.CurrentTaskRunning);
        Assert.Contains("铝锭 A", vm.CurrentTaskDesc);
        Assert.Contains("3003", vm.CurrentTaskDesc);
    }

    [Fact]
    public void RefreshCurrentTask_空闲快照_显示空闲文案()
    {
        var orchestrator = new FakeWorkflowOrchestrator();     // 默认 Idle 快照
        var vm = CreateVm(orchestrator: orchestrator);

        Assert.Equal("当前任务：空闲", vm.CurrentTaskTitle);
        Assert.Equal("● 空闲", vm.CurrentTaskBadgeText);
        Assert.False(vm.CurrentTaskRunning);
    }

    // ==================== SCT 当前策略摘要 ====================

    [Fact]
    public void RefreshPolicySummary_按数量插入_渲染选样规则与间隔与重测上限()
    {
        var policy = new FakeSctPolicyStore
        {
            Current = new SctPolicyOptions   // 默认：ByCount、LeastUsed、轮换启用
            {
                ControlInsertCount = 15,
                ControlRecheckMaxCount = 2,
            },
        };
        var vm = CreateVm(policy: policy);

        vm.OnNavigatedTo();

        Assert.Equal("选样规则：最少使用优先 · 多块轮换", vm.SettingRuleText);
        Assert.Equal("检查样插入：每 15 个生产样 1 次", vm.ControlInsertText);
        Assert.Equal("超差重测：上限 2 次", vm.RecheckLimitText);
    }

    [Fact]
    public void RefreshPolicySummary_按时间插入_渲染分钟间隔()
    {
        var policy = new FakeSctPolicyStore
        {
            Current = new SctPolicyOptions
            {
                ControlInsertMode = ControlInsertMode.ByTime,
                ControlInsertMinutes = 60,
                SettingSampleSelectionMode = SettingSampleSelectionMode.RoundRobin,
                ControlRotationEnabled = false,
            },
        };
        var vm = CreateVm(policy: policy);

        vm.OnNavigatedTo();

        Assert.Equal("选样规则：轮询", vm.SettingRuleText);       // 轮换关闭不追加说明
        Assert.Equal("检查样插入：每 60 分钟 1 次", vm.ControlInsertText);
    }

    // ==================== 报警面板 ====================

    [Fact]
    public void RefreshAlarms_加载报警快照并映射字段()
    {
        var errors = new FakeErrorCollector();
        errors.Collect(1001, "机器人抓取失败", "P-001");
        errors.Collect(5001, "检查样偏差超限");
        var vm = CreateVm(errors: errors);

        vm.OnNavigatedTo();

        Assert.True(vm.HasAlarms);
        Assert.Equal(2, vm.AlarmRows.Count);            // 最新在前
        Assert.Equal("5001", vm.AlarmRows[0].ErrorCodeText);
        Assert.Equal("—", vm.AlarmRows[0].SampleIdText); // 无样品上下文显示占位
        Assert.Equal("P-001", vm.AlarmRows[1].SampleIdText);
    }

    [Fact]
    public void AckAlarm_单条确认后计数同步()
    {
        var errors = new FakeErrorCollector();
        errors.Collect(1001, "抓取失败");
        errors.Collect(1002, "激发失败");
        var vm = CreateVm(errors: errors);
        vm.OnNavigatedTo();

        var acked = vm.AlarmRows[0];
        vm.AckAlarmCommand.Execute(acked.AlarmId);   // 参数类型与 AlarmId（long）一致，绑定传 Int64 不再抛 ArgumentException

        Assert.Equal(1, vm.UnackAlarmCount);
        Assert.True(vm.AlarmRows.Single(r => r.AlarmId == acked.AlarmId).IsAcknowledged);
    }

    [Fact]
    public void AckAllAlarms_全部确认后未确认计数清零()
    {
        var errors = new FakeErrorCollector();
        errors.Collect(1001, "抓取失败");
        errors.Collect(1002, "激发失败");
        var vm = CreateVm(errors: errors);
        vm.OnNavigatedTo();

        vm.AckAllAlarmsCommand.Execute(null);

        Assert.Equal(0, vm.UnackAlarmCount);
        Assert.All(vm.AlarmRows, r => Assert.True(r.IsAcknowledged));
    }

    // ==================== 货架（SCT 5×5 + 分析样 5×5） ====================

    [Fact]
    public void Rack_占用映射与范围外计数()
    {
        var samples = new FakeSampleRepository();
        samples.Store.Add(new SctSample
        {
            SampleId = "TS-001", SampleName = "标样1", SampleType = SampleType.SettingUp,
            AlloyType = "AL", RackPosition = "A01", Status = SampleStatus.Normal,
        });
        samples.Store.Add(new SctSample
        {
            SampleId = "TS-002", SampleName = "标样2", SampleType = SampleType.Control,
            AlloyType = "AL", RackPosition = "B12", Status = SampleStatus.Normal,   // 超出 5×5 示意范围
        });
        samples.Store.Add(new SctSample
        {
            SampleId = "TS-003", SampleName = "标样3", SampleType = SampleType.TypeStd,
            AlloyType = "AL", RackPosition = "A02", Status = SampleStatus.Disabled,
        });
        samples.Store.Add(new SctSample
        {
            SampleId = "TS-004", SampleName = "标样4", SampleType = SampleType.SettingUp,
            AlloyType = "AL", RackPosition = "A03", Status = SampleStatus.NeedMeasure,
        });
        var production = new FakeProductionSampleRepository();
        production.Store.Add(new ProductionSample
        {
            SampleId = "P-100", SampleName = "生产样", RackPosition = "P03",
            Status = ProductionStatus.Pending,
        });
        production.Store.Add(new ProductionSample
        {
            SampleId = "P-101", SampleName = "生产样2", RackPosition = "P14",
            Status = ProductionStatus.Completed,
        });

        var vm = CreateVm(samples: samples, production: production);
        vm.OnNavigatedTo();
        vm.RefreshRack();                           // 货架查询为后台任务，同步再刷一轮保证确定性

        Assert.Equal(5, vm.RackRows.Count);
        Assert.All(vm.RackRows, r => Assert.Equal(5, r.Slots.Count));

        // SCT 货架库位状态着色：可用=Normal（蓝）/ 需测高（黄）/ 禁用（红）/ 空位（灰）；生产样不再混画
        Assert.Equal(RackSlotItem.StateNormal, vm.RackRows[0].Slots[0].SlotState);        // A01 可用
        Assert.Equal(RackSlotItem.StateDisabled, vm.RackRows[0].Slots[1].SlotState);      // A02 禁用
        Assert.True(vm.RackRows[0].Slots[1].IsOccupied);
        Assert.Equal(RackSlotItem.StateNeedMeasure, vm.RackRows[0].Slots[2].SlotState);   // A03 需测高
        Assert.Equal(RackSlotItem.StateEmpty, vm.RackRows[0].Slots[3].SlotState);         // A04 空位
        Assert.False(vm.RackRows[0].Slots[3].IsOccupied);
        Assert.Equal(RackSlotItem.StateEmpty, vm.RackRows[2].Slots[2].SlotState);         // C03 SCT 货架空位（生产样已分卡）

        Assert.Equal(3, vm.RackOccupiedCount);
        Assert.Equal(1, vm.RackOutOfRangeCount);                                 // B12 示意外
        Assert.Equal(25, vm.RackTotalCount);

        // 分析样货架：待分析=黄、已完成=绿（独立编号 P01–P25，行标签 1–5）
        Assert.Equal(5, vm.ProductionRows.Count);
        Assert.All(vm.ProductionRows, r => Assert.Equal(5, r.Slots.Count));
        Assert.Equal(RackSlotItem.StatePending, vm.ProductionRows[0].Slots[2].SlotState);          // P03 待分析
        Assert.Equal("P03", vm.ProductionRows[0].Slots[2].PositionCode);
        Assert.Equal("P-100", vm.ProductionRows[0].Slots[2].SampleIdText);
        Assert.Equal("待分析", vm.ProductionRows[0].Slots[2].StateText);
        Assert.Equal("P-100（待分析）", vm.ProductionRows[0].Slots[2].ToolTipText);
        Assert.Equal(RackSlotItem.StateCompleted, vm.ProductionRows[2].Slots[3].SlotState);        // P14 已完成
        Assert.Equal("已完成", vm.ProductionRows[2].Slots[3].StateText);
        Assert.Equal(RackSlotItem.StateEmpty, vm.ProductionRows[0].Slots[0].SlotState);            // P01 空位

        Assert.Equal(2, vm.ProdOccupiedCount);
        Assert.Equal(0, vm.ProdOutOfRangeCount);
    }

    [Fact]
    public void Rack_人工停用样映射为ManualDisabled_过薄禁用仍为Disabled()
    {
        var samples = new FakeSampleRepository();
        samples.Store.Add(new SctSample
        {
            SampleId = "TS-101", SampleName = "标样1", SampleType = SampleType.SettingUp,
            AlloyType = "AL", RackPosition = "A01", Status = SampleStatus.Normal,
            IsActive = false,   // 人工停用只翻 IsActive 不写 Status → 货架橙色「人工禁用」
        });
        samples.Store.Add(new SctSample
        {
            SampleId = "TS-102", SampleName = "标样2", SampleType = SampleType.Control,
            AlloyType = "AL", RackPosition = "A02", Status = SampleStatus.Disabled,
            IsActive = false,   // 过薄报废：Status=Disabled + IsActive=false → 货架红色「过薄」
        });
        samples.Store.Add(new SctSample
        {
            SampleId = "TS-103", SampleName = "标样3", SampleType = SampleType.Control,
            AlloyType = "AL", RackPosition = "A03", Status = SampleStatus.NeedMeasure,
            IsActive = false,   // 达最大使用次数自动需测高停用：Status 优先 → 货架黄色「需测高」
        });

        var vm = CreateVm(samples: samples);
        vm.OnNavigatedTo();
        vm.RefreshRack();                           // 货架查询为后台任务，同步再刷一轮保证确定性

        Assert.Equal(RackSlotItem.StateManualDisabled, vm.RackRows[0].Slots[0].SlotState);   // A01 人工禁用（正常样人工停用）
        Assert.Equal("人工禁用", vm.RackRows[0].Slots[0].StateText);
        Assert.Equal(RackSlotItem.StateDisabled, vm.RackRows[0].Slots[1].SlotState);         // A02 过薄禁用
        Assert.Equal("过薄", vm.RackRows[0].Slots[1].StateText);
        Assert.Equal(RackSlotItem.StateNeedMeasure, vm.RackRows[0].Slots[2].SlotState);      // A03 需测高（Status 优先）
        Assert.Equal("需测高", vm.RackRows[0].Slots[2].StateText);
        Assert.Equal("TS-103（需测高）", vm.RackRows[0].Slots[2].ToolTipText);
    }

    [Fact]
    public async Task SampleDataChanged_通知后台架即时重载()
    {
        var notifier = new SampleDataChangeNotifier();
        var samples = new FakeSampleRepository();
        samples.Store.Add(new SctSample
        {
            SampleId = "TS-201", SampleName = "标样", SampleType = SampleType.SettingUp,
            AlloyType = "AL", RackPosition = "A01", Status = SampleStatus.Normal, IsActive = true,
        });
        var vm = CreateVm(samples: samples, notifier: notifier);
        vm.OnNavigatedTo();
        vm.RefreshRack();
        Assert.Equal(RackSlotItem.StateNormal, vm.RackRows[0].Slots[0].SlotState);

        // 模拟 SCT 页人工停用后发布通知（不经主控台导航，不等 30 秒定时器）
        samples.Store.Single(s => s.SampleId == "TS-201").IsActive = false;
        notifier.NotifySampleDataChanged();

        // 通知触发的货架刷新在后台线程执行，轮询等待其落回 UI（与真实异步刷新语义一致）
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && vm.RackRows[0].Slots[0].SlotState != RackSlotItem.StateManualDisabled)
        {
            await Task.Delay(10);
        }

        Assert.Equal(RackSlotItem.StateManualDisabled, vm.RackRows[0].Slots[0].SlotState);
        Assert.Equal("人工禁用", vm.RackRows[0].Slots[0].StateText);
    }

    // ==================== 全流程运行日志 ====================

    [Fact]
    public void SystemLog_采集与快照最新在前()
    {
        var log = new SystemLogCollector();
        log.Log("应用", "上位机启动");
        log.Log("设备操作", "任务下发", "Failed");
        log.Log("PLC连接", "Stub 网关", "Warning");

        var entries = log.GetEntries();

        Assert.Equal(3, entries.Count);
        Assert.Equal("PLC连接", entries[0].Category);     // 最新在前
        Assert.Equal("Warning", entries[0].Level);
        Assert.Equal("Failed", entries[1].Level);
        Assert.Equal("应用", entries[2].Category);
    }

    [Fact]
    public void ErrorCollect_联动写入全流程日志()
    {
        var log = new SystemLogCollector();
        var errors = new ErrorCollector(log);

        errors.Collect(1001, "抓取失败", "P-001");

        var entry = Assert.Single(log.GetEntries());
        Assert.Equal("报警", entry.Category);
        Assert.Equal("Failed", entry.Level);
        Assert.Contains("1001", entry.Message);
        Assert.Contains("P-001", entry.Message);
    }

    [Fact]
    public void SystemLog_封顶淘汰最旧()
    {
        var log = new SystemLogCollector();
        for (var i = 0; i < 505; i++)
        {
            log.Log("测试", $"第 {i} 条");
        }

        var entries = log.GetEntries();
        Assert.Equal(500, entries.Count);              // 封顶 500
        Assert.Contains("第 504 条", entries[0].Message); // 最新保留
        Assert.DoesNotContain(entries, e => e.Message == "第 1 条"); // 最旧淘汰
    }
}
