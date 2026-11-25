using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.SampleManager;
using SpectroscopicAnalysis.Host.SctStrategy;

namespace SpectroscopicAnalysis.Tests;

/// <summary>样品注册服务测试：四类样品校验规则 / 编号与架位查重 / 注册成功自动入队映射（与样品档案页注册面板规则一致）。</summary>
public sealed class SampleRegistrationServiceTests
{
    private readonly FakeSampleRepository _samples = new();
    private readonly FakeProductionSampleRepository _production = new();
    private readonly FakeSctStrategyEngine _engine = new();
    private readonly FakeErrorCollector _errors = new();

    private SampleRegistrationService Create() => new(_samples, _production, _engine, _errors, new SystemLogCollector());

    private static SampleRegisterForm MakeProductionForm(string id = "P-001", string alloy = "3003") => new()
    {
        SampleType = SampleType.Production,
        SampleId = id,
        SampleName = "生产样",
        AnalysisMethod = "GB/T 7999-A",
        AlloyType = alloy,
    };

    private static SampleRegisterForm MakeSctForm(string type, string id = "SUS-001", string alloy = "") => new()
    {
        SampleType = type,
        SampleId = id,
        SampleName = "标样",
        AlloyType = alloy,
        RackPosition = "A-01",
        InitialThickness = "10",
        MinThickness = "5",
        MaxUsageCount = "50",
    };

    // ==================== 校验规则 ====================

    [Fact]
    public async Task 生产样_编号名称方法齐全时入库并自动入队()
    {
        var error = await Create().RegisterAsync(MakeProductionForm());

        Assert.Null(error);
        Assert.Single(_production.Store);
        Assert.Equal(ProductionStatus.Pending, _production.Store[0].Status);
        Assert.Equal("GB/T 7999-A", _production.Store[0].AssociatedMethod);
        Assert.Equal("3003", _production.Store[0].AlloyType);   // 生产样合金必填并落库
        Assert.Single(_engine.Submitted);
        Assert.Equal(SctTaskType.Production, _engine.Submitted[0].TaskType);
        Assert.Equal("P-001", _engine.Submitted[0].SampleId);
    }

    [Fact]
    public async Task 生产样_缺分析方法被拒且不进错误链()
    {
        var form = MakeProductionForm();
        form.AnalysisMethod = null;

        var error = await Create().RegisterAsync(form);

        Assert.Equal("请选择分析方法", error);
        Assert.Empty(_production.Store);
        Assert.Empty(_errors.Collected);        // 填写类错误仅 UI 提示，不写日志与报警面板
    }

    [Fact]
    public async Task 生产样_缺合金类型被拒且不进错误链()
    {
        var error = await Create().RegisterAsync(MakeProductionForm(alloy: ""));

        Assert.Equal("请填写合金类型", error);
        Assert.Empty(_production.Store);
        Assert.Empty(_errors.Collected);
    }

    [Fact]
    public async Task 控样_架位厚度使用次数齐全时入库初始化并入队标准化()
    {
        var error = await Create().RegisterAsync(MakeSctForm(SampleType.SettingUp));

        Assert.Null(error);
        var sample = _samples.Store.Single();
        Assert.Equal(SampleType.SettingUp, sample.SampleType);
        Assert.Equal(10, sample.CurrentThickness);      // 入库初始化：当前厚度 = 初始厚度
        Assert.Equal(0, sample.UsageCount);
        Assert.Equal(SampleStatus.Normal, sample.Status);
        Assert.True(sample.IsActive);
        Assert.Equal(SctTaskType.Standardize, _engine.Submitted.Single().TaskType);
    }

    [Fact]
    public async Task 检查样_入队控制检查()
    {
        var error = await Create().RegisterAsync(MakeSctForm(SampleType.Control, "CTL-001"));

        Assert.Null(error);
        Assert.Equal(SctTaskType.ControlCheck, _engine.Submitted.Single().TaskType);
    }

    [Fact]
    public async Task 类型标准化样_合金必填并映射类型标准化任务()
    {
        var error = await Create().RegisterAsync(MakeSctForm(SampleType.TypeStd, "TYP-001", alloy: "3003"));

        Assert.Null(error);
        Assert.Equal("3003", _samples.Store.Single().AlloyType);
        Assert.Equal(SctTaskType.TypeStandardize, _engine.Submitted.Single().TaskType);
    }

    [Fact]
    public async Task 类型标准化样_缺合金被拒()
    {
        var error = await Create().RegisterAsync(MakeSctForm(SampleType.TypeStd, "TYP-001"));

        Assert.Equal("类型标准化样必须填写关联合金", error);
        Assert.Empty(_samples.Store);
    }

    [Fact]
    public async Task 控样_填写合金被拒()
    {
        var error = await Create().RegisterAsync(MakeSctForm(SampleType.SettingUp, alloy: "3003"));

        Assert.Equal("仅类型标准化样/生产样可关联合金，控样/检查样请留空合金", error);
    }

    [Theory]
    [InlineData("", "5", "50", "样品架位置不能为空")]
    [InlineData("A-01", "0", "50", "初始厚度必须为大于 0 的数值")]
    [InlineData("A-01", "abc", "50", "初始厚度必须为大于 0 的数值")]
    [InlineData("A-01", "5", "50", "初始厚度必须大于最小厚度")]
    [InlineData("A-01", "10", "0", "最大使用次数必须为不小于 1 的整数")]
    public async Task SCT样_厚度与使用次数校验(string rack, string init, string maxUsage, string expected)
    {
        var form = MakeSctForm(SampleType.SettingUp);
        form.RackPosition = rack;
        form.InitialThickness = init;
        form.MinThickness = "5";
        form.MaxUsageCount = maxUsage;

        var error = await Create().RegisterAsync(form);

        Assert.Equal(expected, error);
        Assert.Empty(_samples.Store);
    }

    // ==================== 查重 ====================

    [Fact]
    public async Task 生产样_编号重复被拒()
    {
        var service = Create();
        await service.RegisterAsync(MakeProductionForm());

        var error = await service.RegisterAsync(MakeProductionForm());

        Assert.Equal("样品编号已存在：P-001", error);
        Assert.Single(_production.Store);
    }

    [Fact]
    public async Task SCT样_编号重复被拒()
    {
        var service = Create();
        await service.RegisterAsync(MakeSctForm(SampleType.SettingUp));

        var error = await service.RegisterAsync(MakeSctForm(SampleType.SettingUp));

        // 同一表单重复提交：编号重复 + 架位占用两条错误一并返回
        Assert.Contains("样品编号已存在：SUS-001", error);
        Assert.Contains("货架位置已占用：A-01", error);
        Assert.Single(_samples.Store);
        Assert.Empty(_errors.Collected);        // 重复同属填写类错误，不进错误链
    }

    [Fact]
    public async Task SCT样_架位被其他样品占用被拒()
    {
        var service = Create();
        await service.RegisterAsync(MakeSctForm(SampleType.SettingUp, "SUS-001"));

        var error = await service.RegisterAsync(MakeSctForm(SampleType.Control, "CTL-001"));

        Assert.Contains("货架位置已占用：A-01", error);
        Assert.Single(_samples.Store);
    }
}
