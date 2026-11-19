using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.SctStrategy;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>SCT 页手动触发按钮接入测试：命令可用性 + 提交策略引擎任务队列。</summary>
public sealed class SctManualTriggerTests
{
    private readonly FakeSctPolicyStore _policy = new();
    private readonly FakeSampleRepository _samples = new();
    private readonly FakeOperationLogRepository _logs = new();
    private readonly FakeErrorCollector _errors = new();

    private SctViewModel Create(SctStrategyEngine? engine) => new(
        _samples, _logs, _policy, new FakeUiDispatcher(), new FakeDialogService(), _errors, engine);

    [Fact]
    public void 引擎未注入_三按钮保持禁用()
    {
        var vm = Create(null);

        Assert.False(vm.TriggerStandardizationCommand.CanExecute(null));
        Assert.False(vm.TriggerControlCheckCommand.CanExecute(null));
        Assert.False(vm.TriggerHeightMeasureCommand.CanExecute(null));
    }

    [Fact]
    public async Task 引擎注入_标准化与控制检查可触发并入队()
    {
        _samples.Store.Add(MakeSample("SUS-001"));                       // 控样（默认类型）
        _samples.Store.Add(MakeSample("CTL-001", SampleType.Control));
        var engine = new SctStrategyEngine(_policy, _samples, _logs, _errors);
        var vm = Create(engine);
        await vm.LoadCommand.ExecuteAsync(null);

        // 手动标准化仅限控样/类型标准化样（e50ef9f），需选中控样行
        vm.SelectedSample = vm.SctSamples.First(s => s.SampleId == "SUS-001");
        Assert.True(vm.TriggerStandardizationCommand.CanExecute(null));
        vm.TriggerStandardizationCommand.Execute(null);

        // 手动控制检查仅限检查样（e50ef9f），切换选中检查样行
        vm.SelectedSample = vm.SctSamples.First(s => s.SampleId == "CTL-001");
        Assert.True(vm.TriggerControlCheckCommand.CanExecute(null));
        vm.TriggerControlCheckCommand.Execute(null);

        Assert.True(engine.TryDequeue(out var t1));
        Assert.Equal(SctTaskType.Standardize, t1.TaskType);
        engine.CompleteTask(t1.TaskId, true, null);
        Assert.True(engine.TryDequeue(out var t2));
        Assert.Equal(SctTaskType.ControlCheck, t2.TaskType);
    }

    [Fact]
    public async Task 类型标准化样_可触发校正检查()
    {
        _samples.Store.Add(MakeSample("TS-001", SampleType.TypeStd));
        var engine = new SctStrategyEngine(_policy, _samples, _logs, _errors);
        var vm = Create(engine);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SelectedSample = vm.SctSamples.First(s => s.SampleId == "TS-001");
        Assert.True(vm.TriggerControlCheckCommand.CanExecute(null));
        vm.TriggerControlCheckCommand.Execute(null);

        Assert.True(engine.TryDequeue(out var t));
        Assert.Equal(SctTaskType.ControlCheck, t.TaskType);
        Assert.Equal("TS-001", t.SampleId);
    }

    [Fact]
    public async Task 检查样_可触发类型标准化并带显式样品()
    {
        _samples.Store.Add(MakeSample("CTL-001", SampleType.Control));
        var engine = new SctStrategyEngine(_policy, _samples, _logs, _errors);
        var vm = Create(engine);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SelectedSample = vm.SctSamples.First(s => s.SampleId == "CTL-001");
        Assert.True(vm.TriggerTypeStandardizeCommand.CanExecute(null));
        vm.TriggerTypeStandardizeCommand.Execute(null);

        Assert.True(engine.TryDequeue(out var t));
        Assert.Equal(SctTaskType.TypeStandardize, t.TaskType);
        Assert.Equal("CTL-001", t.SampleId);
    }

    [Fact]
    public async Task 控样_触发类型标准化被拒()
    {
        _samples.Store.Add(MakeSample("SUS-001"));
        var engine = new SctStrategyEngine(_policy, _samples, _logs, _errors);
        var vm = Create(engine);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SelectedSample = vm.SctSamples.First(s => s.SampleId == "SUS-001");
        vm.TriggerTypeStandardizeCommand.Execute(null);

        Assert.False(engine.TryDequeue(out _));
        Assert.Equal("仅检查样或类型标准化样可手动触发类型标准化", vm.TriggerError);
    }

    [Fact]
    public async Task 测高触发_未选中行禁用_选中后入队带目标样品()
    {
        _samples.Store.Add(MakeSample("SUS-001"));
        var engine = new SctStrategyEngine(_policy, _samples, _logs, _errors);
        var vm = Create(engine);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.False(vm.TriggerHeightMeasureCommand.CanExecute(null));      // 未选中行

        vm.SelectedSample = vm.SctSamples.First();
        Assert.True(vm.TriggerHeightMeasureCommand.CanExecute(null));
        vm.TriggerHeightMeasureCommand.Execute(null);

        Assert.True(engine.TryDequeue(out var task));
        Assert.Equal(SctTaskType.HeightMeasure, task.TaskType);
        Assert.Equal("SUS-001", task.SampleId);
    }

    [Fact]
    public async Task 去重合并_重复点击标准化只保留一个任务()
    {
        _samples.Store.Add(MakeSample("SUS-001"));
        var engine = new SctStrategyEngine(_policy, _samples, _logs, _errors);
        var vm = Create(engine);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SelectedSample = vm.SctSamples.First();      // 选中控样行后触发
        vm.TriggerStandardizationCommand.Execute(null);
        vm.TriggerStandardizationCommand.Execute(null);

        Assert.True(engine.TryDequeue(out _));
        Assert.False(engine.TryDequeue(out _));                             // 第二次被去重合并
    }

    private static SctSample MakeSample(string id, string sampleType = SampleType.SettingUp) => new()
    {
        SampleId = id,
        SampleName = id,
        SampleType = sampleType,
        Status = SampleStatus.Normal,
        MinThickness = 5,
        MaxUsageCount = 50,
        IsActive = true,
    };
}
