using CommunityToolkit.Mvvm.ComponentModel;
using System.Linq;
using SpectroscopicAnalysis.Host.ViewModels;
using SpectroscopicAnalysis.Host.Vision;

namespace SpectroscopicAnalysis.Tests;

/// <summary>
/// 主窗口 ViewModel 测试：导航切换与缩放档位（开发计划 D2 验收项）。
/// </summary>
public sealed class MainViewModelTests
{
    private static MainViewModel CreateMain() => new(
        new ProductionMonitorViewModel(new FakeSctOperationLogRepository(), new FakeSampleRepository(), new FakeProductionSampleRepository(), new FakeSctStrategyEngine(), new FakeWorkflowOrchestrator(), new FakeUiDispatcher(), new FakeErrorCollector(), new FakeDialogService(), new FakeSampleRegistrationService()),
        new ConsoleViewModel(new FakeErrorCollector()),
        new SamplesViewModel(new FakeSampleRepository(), new FakeProductionSampleRepository(), new FakeCertifiedValueRepository(), new FakeControlLimitRepository(),
            new FakeCoefficientRepository(), new FakeOperationLogRepository(), new FakeSctStrategyEngine(),
            new FakeUiDispatcher(), new FakeDialogService(), new FakeErrorCollector()),
        new SctViewModel(new FakeSampleRepository(), new FakeOperationLogRepository(), new FakeSctPolicyStore(),
            new FakeUiDispatcher(), new FakeDialogService(), new FakeErrorCollector()),
        new CoefficientsViewModel(new FakeCoefficientRepository(), new FakeOperationLogRepository(), new FakeUiDispatcher(), new FakeErrorCollector()),
        new AlarmsViewModel(),
        new SettingsViewModel(),
        new LaserMarkingViewModel(new FakeLaserMarkingService()));

    [Fact]
    public void 构造后_默认落在生产监控且分组展开()
    {
        var main = CreateMain();

        Assert.IsType<ProductionMonitorViewModel>(main.CurrentViewModel);
        var group = main.NavItems[0];
        Assert.Equal("主控台 · 生产监控", group.Title);      // 首项为二级分组
        Assert.NotNull(group.Children);
        Assert.Equal(2, group.Children.Count);
        Assert.True(group.Children[1].IsSelected);           // 默认选中「生产监控」子项（子项顺序：主控台在前）
        Assert.Equal("生产监控", group.Children[1].Title);
        Assert.Equal("主控台", group.Children[0].Title);
        Assert.True(group.IsExpanded);                       // 子项导航自动展开分组
    }

    [Fact]
    public void NavigateTo_二级子项切换对应页面()
    {
        var main = CreateMain();
        var consoleItem = main.NavItems[0].Children!.Single(n => n.Title == "主控台");

        main.NavigateToCommand.Execute(consoleItem);

        Assert.IsType<ConsoleViewModel>(main.CurrentViewModel);
        Assert.True(consoleItem.IsSelected);
        Assert.False(main.NavItems[0].Children![1].IsSelected); // 生产监控子项取消选中
    }

    [Fact]
    public void NavigateTo_切换页面并维护选中态()
    {
        var main = CreateMain();
        var target = main.NavItems.First(n => n.Title == "SCT 管理");

        main.NavigateToCommand.Execute(target);

        Assert.IsType<SctViewModel>(main.CurrentViewModel);
        Assert.True(target.IsSelected);
        Assert.False(main.NavItems[0].Children![1].IsSelected); // 原选中项（生产监控子项）取消
    }

    [Fact]
    public void NavigateTo_空参数不抛异常()
    {
        var main = CreateMain();
        main.NavigateToCommand.Execute(null); // 不应抛异常，状态不变
        Assert.IsType<ProductionMonitorViewModel>(main.CurrentViewModel);
    }

    [Theory]
    [InlineData("1.0", 1.0)]
    [InlineData("1.25", 1.25)]
    [InlineData("1.5", 1.5)]
    public void SetScale_合法档位生效(string scaleText, double expected)
    {
        var main = CreateMain();
        main.SetScaleCommand.Execute(scaleText);
        Assert.Equal(expected, main.UiScale);
    }

    [Theory]
    [InlineData("2.0")]   // 不在档位
    [InlineData("abc")]   // 非数字
    [InlineData(null)]    // 空参数
    public void SetScale_非法档位被拒绝(string? scaleText)
    {
        var main = CreateMain();
        main.SetScaleCommand.Execute(scaleText);
        Assert.Equal(1.0, main.UiScale);   // 保持原档位
    }
}
