using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.SctStrategy;
using SpectroscopicAnalysis.Host.ViewModels;
using SpectroscopicAnalysis.Tests;
using SpectroscopicAnalysis.Tests.Data;

namespace SpectroscopicAnalysis.Tests;

/// <summary>样品注册测试：入库初始化 / 校验拒绝 / 重复编号 / 取消（开发规范第 5 节 + 任务清单入库初始化）。</summary>
public sealed class SamplesRegisterTests
{
    /// <summary>填入合法表单值。</summary>
    private static SamplesViewModel CreateValidForm(FakeSampleRepository repo, FakeSctStrategyEngine? engine = null)
    {
        var vm = new SamplesViewModel(repo, new FakeProductionSampleRepository(), new FakeCertifiedValueRepository(), new FakeControlLimitRepository(),
            new FakeCoefficientRepository(), new FakeOperationLogRepository(), engine ?? new FakeSctStrategyEngine(),
            new FakeUiDispatcher(), new FakeDialogService(), new FakeErrorCollector());
        vm.RegSampleId = "REG-001";
        vm.RegSampleName = "注册测试样品";
        vm.RegSampleType = SampleType.SettingUp;
        vm.RegAlloyType = null;   // 控样/检查样不关联合金（仅 TypeStd 可挂）
        vm.RegRackPosition = "D01";
        vm.RegInitialThickness = "15.5";
        vm.RegMinThickness = "6.0";
        vm.RegMaxUsageCount = "40";
        return vm;
    }

    [Fact]
    public async Task 合法注册_入库初始化字段正确()
    {
        var repo = new FakeSampleRepository();
        var vm = CreateValidForm(repo);

        await vm.RegisterCommand.ExecuteAsync(null);

        var inserted = repo.Store.Single(s => s.SampleId == "REG-001");
        Assert.Equal("注册测试样品", inserted.SampleName);
        Assert.Equal(SampleType.SettingUp, inserted.SampleType);
        Assert.Equal(15.5, inserted.InitialThickness);
        Assert.Equal(15.5, inserted.CurrentThickness); // 入库初始化：当前厚度 = 初始厚度
        Assert.Equal(6.0, inserted.MinThickness);
        Assert.Equal(0, inserted.UsageCount);          // 入库初始化：使用次数 = 0
        Assert.Equal(SampleStatus.Normal, inserted.Status); // 入库初始化：状态 = Normal
        Assert.True(inserted.IsActive);
        Assert.Null(inserted.AlloyType); // 控样不关联合金
        Assert.Null(vm.RegisterError);
        Assert.False(vm.IsRegisterOpen); // 成功后关闭面板
    }

    [Fact]
    public async Task 非类型标准化样填合金_拒绝()
    {
        var repo = new FakeSampleRepository();
        var vm = CreateValidForm(repo);
        vm.RegAlloyType = "6xxx";   // 控样不允许挂合金

        await vm.RegisterCommand.ExecuteAsync(null);

        Assert.NotNull(vm.RegisterError);
        Assert.Contains("仅类型标准化样/生产样可关联合金", vm.RegisterError);
        Assert.Empty(repo.Store);
    }

    [Fact]
    public async Task 类型标准化样填合金_允许入库()
    {
        var repo = new FakeSampleRepository();
        var vm = CreateValidForm(repo);
        vm.RegSampleType = SampleType.TypeStd;
        vm.RegAlloyType = "6061";

        await vm.RegisterCommand.ExecuteAsync(null);

        Assert.Null(vm.RegisterError);
        var inserted = repo.Store.Single(s => s.SampleId == "REG-001");
        Assert.Equal("6061", inserted.AlloyType);
    }

    [Fact]
    public async Task 重复编号_拒绝且不新增()
    {
        var repo = new FakeSampleRepository();
        repo.Store.Add(MakeSample("SUS-001", SampleType.SettingUp));
        var vm = CreateValidForm(repo);
        vm.RegSampleId = "SUS-001";
        vm.IsRegisterOpen = true; // 前置：面板已打开

        await vm.RegisterCommand.ExecuteAsync(null);

        Assert.NotNull(vm.RegisterError);
        Assert.Contains("已存在", vm.RegisterError);
        Assert.Single(repo.Store); // 未新增
        Assert.True(vm.IsRegisterOpen); // 面板保持打开等待修正
    }

    [Fact]
    public async Task 货架位置重复_拒绝且提示占用样品()
    {
        var repo = new FakeSampleRepository();
        repo.Store.Add(MakeSample("SUS-001", SampleType.SettingUp));
        var vm = CreateValidForm(repo);
        vm.RegRackPosition = "A01"; // 与 SUS-001 重复

        await vm.RegisterCommand.ExecuteAsync(null);

        Assert.NotNull(vm.RegisterError);
        Assert.Contains("货架位置已占用", vm.RegisterError);
        Assert.Contains("SUS-001", vm.RegisterError);
        Assert.Single(repo.Store); // 未新增
    }

    [Fact]
    public async Task 初始厚度不大于最小厚度_拒绝()
    {
        var repo = new FakeSampleRepository();
        var vm = CreateValidForm(repo);
        vm.RegInitialThickness = "6.0";
        vm.RegMinThickness = "6.0"; // 相等也应拒绝

        await vm.RegisterCommand.ExecuteAsync(null);

        Assert.Equal("初始厚度必须大于最小厚度", vm.RegisterError);
        Assert.Empty(repo.Store);
    }

    [Fact]
    public async Task 编号与架位同时重复_错误聚合提示()
    {
        var repo = new FakeSampleRepository();
        repo.Store.Add(MakeSample("SUS-001", SampleType.SettingUp));
        var vm = CreateValidForm(repo);
        vm.RegSampleId = "SUS-001";
        vm.RegRackPosition = "A01";

        await vm.RegisterCommand.ExecuteAsync(null);

        Assert.NotNull(vm.RegisterError);
        Assert.Contains("样品编号已存在", vm.RegisterError, StringComparison.Ordinal);
        Assert.Contains("货架位置已占用", vm.RegisterError, StringComparison.Ordinal);
        Assert.Single(repo.Store);
    }

    [Fact]
    public async Task 编号缺失_拒绝且不新增()
    {
        var repo = new FakeSampleRepository();
        var vm = CreateValidForm(repo);
        vm.RegSampleId = "";

        await vm.RegisterCommand.ExecuteAsync(null);

        Assert.Equal("样品编号不能为空", vm.RegisterError);
        Assert.Empty(repo.Store);
    }

    [Fact]
    public async Task 架位缺失_拒绝且不新增()
    {
        var repo = new FakeSampleRepository();
        var vm = CreateValidForm(repo);
        vm.RegRackPosition = "";

        await vm.RegisterCommand.ExecuteAsync(null);

        Assert.Equal("样品架位置不能为空", vm.RegisterError);
        Assert.Empty(repo.Store);
    }

    [Fact]
    public void 名称缺失_校验拒绝()
    {
        var repo = new FakeSampleRepository();
        var vm = CreateValidForm(repo);
        vm.RegSampleName = "";
        vm.RegisterCommand.Execute(null);
        Assert.Equal("样品名称不能为空", vm.RegisterError);
        Assert.Empty(repo.Store);
    }

    [Theory]
    [InlineData("0")]      // 厚度为零
    [InlineData("-1")]     // 厚度为负
    [InlineData("abc")]    // 厚度非数字
    public async Task 初始厚度非法_拒绝且不新增(string thickness)
    {
        var repo = new FakeSampleRepository();
        var vm = CreateValidForm(repo);
        vm.RegInitialThickness = thickness;

        await vm.RegisterCommand.ExecuteAsync(null);

        Assert.Equal("初始厚度必须为大于 0 的数值", vm.RegisterError);
        Assert.Empty(repo.Store);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("1.5")] // 非整数
    public async Task 最大使用次数非法_拒绝且不新增(string maxUsage)
    {
        var repo = new FakeSampleRepository();
        var vm = CreateValidForm(repo);
        vm.RegMaxUsageCount = maxUsage;

        await vm.RegisterCommand.ExecuteAsync(null);

        Assert.Equal("最大使用次数必须为不小于 1 的整数", vm.RegisterError);
        Assert.Empty(repo.Store);
    }

    [Fact]
    public async Task 注册成功后_列表刷新包含新样品()
    {
        var repo = new FakeSampleRepository();
        var vm = CreateValidForm(repo);
        await vm.LoadCommand.ExecuteAsync(null); // 先完成初始加载
        var before = vm.SctSamples.Count;

        await vm.RegisterCommand.ExecuteAsync(null);

        Assert.Equal(before + 1, vm.SctSamples.Count);
        Assert.NotNull(vm.SctSamples.FirstOrDefault(r => r.SampleId == "REG-001"));
    }

    [Fact]
    public void CancelRegister_清空表单错误并关闭()
    {
        var repo = new FakeSampleRepository();
        var vm = CreateValidForm(repo);
        vm.IsRegisterOpen = true;
        vm.RegisterError = "某错误";

        vm.CancelRegisterCommand.Execute(null);

        Assert.False(vm.IsRegisterOpen);
        Assert.Null(vm.RegisterError);
        Assert.Equal(string.Empty, vm.RegSampleId);
    }

    private static SctSample MakeSample(string id, string type) => new()
    {
        SampleId = id,
        SampleName = id,
        SampleType = type,
        RackPosition = "A01",
        InitialThickness = 12.0,
        CurrentThickness = 12.0,
        MinThickness = 8.0,
        UsageCount = 0,
        MaxUsageCount = 50,
        Status = SampleStatus.Normal,
    };

    [Fact]
    public async Task 注册完成_自动加入任务队列_任务类型按样品类型映射()
    {
        var engine = new FakeSctStrategyEngine();
        var repo = new FakeSampleRepository();
        var vm = CreateValidForm(repo, engine);   // 控样
        await vm.RegisterCommand.ExecuteAsync(null);

        var prodRepo = new FakeProductionSampleRepository();
        var prodEngine = new FakeSctStrategyEngine();
        var prodVm = new SamplesViewModel(new FakeSampleRepository(), prodRepo, new FakeCertifiedValueRepository(), new FakeControlLimitRepository(),
            new FakeCoefficientRepository(), new FakeOperationLogRepository(), prodEngine,
            new FakeUiDispatcher(), new FakeDialogService(), new FakeErrorCollector())
        {
            RegSampleId = "PRD-REG-001",
            RegSampleName = "注册生产样",
            RegSampleType = SampleType.Production,
            RegAnalysisMethod = "GB/T 7999-A",
            RegAlloyType = "3003",   // 生产样合金必填
        };
        await prodVm.RegisterCommand.ExecuteAsync(null);

        Assert.Single(engine.Submitted);
        Assert.Equal((SctTaskType.Standardize, "REG-001"), (engine.Submitted[0].TaskType, engine.Submitted[0].SampleId));
        Assert.Single(prodEngine.Submitted);
        Assert.Equal((SctTaskType.Production, "PRD-REG-001"), (prodEngine.Submitted[0].TaskType, prodEngine.Submitted[0].SampleId));
   }
}
