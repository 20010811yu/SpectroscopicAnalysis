using CommunityToolkit.Mvvm.ComponentModel;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>主控台 ViewModel 测试：报警面板确认、全流程日志映射、货架示意映射（5×8）。</summary>
public sealed class ConsoleViewModelTests
{
    private static ConsoleViewModel CreateVm(
        FakeErrorCollector? errors = null,
        FakeSampleRepository? samples = null,
        FakeProductionSampleRepository? production = null)
    {
        return new ConsoleViewModel(errors ?? new FakeErrorCollector(), new SystemLogCollector(),
            samples ?? new FakeSampleRepository(), production ?? new FakeProductionSampleRepository(),
            new FakeUiDispatcher(), new LaserMarkingViewModel(new FakeLaserMarkingService()));
    }

    // ==================== 报警面板 ====================

    [Fact]
    public void RefreshAlarms_加载报警快照并映射字段()
    {
        var errors = new FakeErrorCollector();
        errors.Collect(1001, "机器人抓取失败", "P-001");
        errors.Collect(5001, "控制样偏差超限");
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
        vm.AckAlarmCommand.Execute(acked.AlarmId.ToString());

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

    // ==================== 货架示意（5×8） ====================

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
            AlloyType = "AL", RackPosition = "B12", Status = SampleStatus.Normal,   // 超出 5×8 示意范围
        });
        var production = new FakeProductionSampleRepository();
        production.Store.Add(new ProductionSample
        {
            SampleId = "P-100", SampleName = "生产样", RackPosition = "C03",
            Status = ProductionStatus.Pending,
        });

        var vm = CreateVm(samples: samples, production: production);
        vm.OnNavigatedTo();
        vm.RefreshRack();                           // 货架查询为后台任务，同步再刷一轮保证确定性

        Assert.Equal(5, vm.RackRows.Count);
        Assert.All(vm.RackRows, r => Assert.Equal(8, r.Slots.Count));
        Assert.Equal("TS-001", vm.RackRows[0].Slots[0].SampleIdText);           // A01 占用
        Assert.True(vm.RackRows[0].Slots[0].IsOccupied);
        Assert.False(vm.RackRows[0].Slots[1].IsOccupied);                        // A02 空闲
        Assert.Equal("P-100", vm.RackRows[2].Slots[2].SampleIdText);            // C03 占用
        Assert.Equal(2, vm.RackOccupiedCount);
        Assert.Equal(1, vm.RackOutOfRangeCount);                                 // B12 示意外
        Assert.Equal(40, vm.RackTotalCount);
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
