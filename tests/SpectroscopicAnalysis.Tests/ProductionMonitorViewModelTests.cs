using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.SampleManager;
using SpectroscopicAnalysis.Host.SctStrategy;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>生产监控 ViewModel 测试：样品注册弹窗入口与注册后队列/KPI 刷新。</summary>
public sealed class ProductionMonitorViewModelTests
{
    private static ProductionMonitorViewModel CreateVm(
        FakeDialogService? dialogs = null,
        FakeSampleRegistrationService? registration = null)
    {
        var dialogSvc = dialogs ?? new FakeDialogService();
        var regSvc = registration ?? new FakeSampleRegistrationService();
        return new ProductionMonitorViewModel(new FakeSampleRepository(), new FakeProductionSampleRepository(),
            new FakeSctStrategyEngine(), new FakeWorkflowOrchestrator(), new FakeUiDispatcher(), new FakeErrorCollector(),
            dialogSvc, regSvc);
    }

    [Fact]
    public void OpenSampleRegister_弹出注册表单并传入提交委托()
    {
        var dialogs = new FakeDialogService();
        var vm = CreateVm(dialogs: dialogs);

        vm.OpenSampleRegisterCommand.Execute(null);

        Assert.Single(dialogs.RegisterForms);       // 弹窗打开且表单 VM 传入
    }

    [Fact]
    public async Task OpenSampleRegister_确认入库后刷新队列与KPI()
    {
        var dialogs = new FakeDialogService();
        // 用真实注册服务（Fake 仓储）+ 真实策略引擎：注册入库并自动入队后，队列/KPI 可断言任务数
        var productionRepo = new FakeProductionSampleRepository();
        var samplesRepo = new FakeSampleRepository();
        var engine = new SctStrategyEngine(new FakeSctPolicyStore(), samplesRepo,
            new FakeSctOperationLogRepository(), new FakeErrorCollector());
        var registration = new SampleRegistrationService(samplesRepo, productionRepo, engine, new FakeErrorCollector());
        var vm = new ProductionMonitorViewModel(samplesRepo, productionRepo, engine,
            new FakeWorkflowOrchestrator(), new FakeUiDispatcher(), new FakeErrorCollector(), dialogs, registration);

        // 模拟模态弹窗交互：弹窗显示期间填写表单并点确认入库（真实弹窗确认发生在返回之前）
        dialogs.OnShown = async form =>
        {
            form.SampleId = "P-100";
            form.SampleName = "测试样品";
            form.AnalysisMethod = "GB/T 7999-A";
            await form.ConfirmCommand.ExecuteAsync(null);
        };

        vm.OpenSampleRegisterCommand.Execute(null);
        await WaitQueueAsync(vm);                   // 队列/KPI 为 fire-and-forget，轮询等待两者都落到 UI

        var form = dialogs.RegisterForms.Single();
        Assert.True(form.Confirmed);                // 注册成功
        Assert.Single(productionRepo.Store);        // 生产样入库
        Assert.Equal(1, vm.QueueTotalCount);        // 注册自动入队后队列卡刷新
        Assert.Equal(1, vm.QueuePendingCount);      // KPI 待分析同步刷新
    }

    [Fact]
    public async Task Confirm_注册服务报错时留在弹窗显示错误()
    {
        var dialogs = new FakeDialogService();
        var registration = new FakeSampleRegistrationService { NextError = "样品编号已存在" };
        var vm = CreateVm(dialogs: dialogs, registration: registration);

        vm.OpenSampleRegisterCommand.Execute(null);
        var form = dialogs.RegisterForms[0];
        await form.ConfirmCommand.ExecuteAsync(null);

        Assert.False(form.Confirmed);
        Assert.Equal("样品编号已存在", form.RegisterError);
    }

    /// <summary>等待 fire-and-forget 的队列/KPI 刷新完成（后台取数 + Dispatcher 回 UI，轮询直到两者非默认值或超时）。</summary>
    private static async Task WaitQueueAsync(ProductionMonitorViewModel vm)
    {
        for (var i = 0; i < 100 && (vm.QueueTotalCount == 0 || vm.QueuePendingCount == 0); i++)
        {
            await Task.Delay(10);
        }
    }
}
