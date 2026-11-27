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
        RackPosition = "C-01",
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
        Assert.Equal("C-01", _production.Store[0].RackPosition); // 生产样架位必选并落库
        Assert.Single(_engine.Submitted);
        Assert.Equal(SctTaskType.Production, _engine.Submitted[0].TaskType);
        Assert.Equal("P-001", _engine.Submitted[0].SampleId);
    }

    [Fact]
    public async Task 生产样_缺架位被拒且不进错误链()
    {
        var form = MakeProductionForm();
        form.RackPosition = "";

        var error = await Create().RegisterAsync(form);

        Assert.Equal("样品架位置不能为空", error);
        Assert.Empty(_production.Store);
        Assert.Empty(_errors.Collected);
    }

    [Fact]
    public async Task 生产样_架位被SCT样占用被拒()
    {
        _samples.Store.Add(new SctSample
        {
            SampleId = "SUS-001", SampleName = "标样", SampleType = SampleType.SettingUp,
            RackPosition = "C-01", InitialThickness = 10, CurrentThickness = 10,
            MinThickness = 5, MaxUsageCount = 50, Status = SampleStatus.Normal,
        });

        var error = await Create().RegisterAsync(MakeProductionForm());

        Assert.Contains("货架位置已占用：C-01（现有样品 SUS-001）", error);
        Assert.Empty(_production.Store);
    }

    [Fact]
    public async Task 生产样_架位被其他生产样占用被拒()
    {
        var service = Create();
        await service.RegisterAsync(MakeProductionForm());

        var error = await service.RegisterAsync(MakeProductionForm("P-002"));

        Assert.Contains("货架位置已占用：C-01（现有样品 P-001）", error);
        Assert.Single(_production.Store);
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
    public async Task 控样_未填初始厚度注册置需测高厚度未知()
    {
        var form = MakeSctForm(SampleType.SettingUp);
        form.InitialThickness = "";                          // 初始厚度留空（非必填）

        var error = await Create().RegisterAsync(form);

        Assert.Null(error);
        var sample = _samples.Store.Single();
        Assert.Equal(0, sample.CurrentThickness);            // 厚度未知存 0 哨兵（展示层显示 "—"）
        Assert.Equal(0, sample.InitialThickness);
        Assert.Equal(SampleStatus.NeedMeasure, sample.Status);   // 强制首次使用前测高（编排器前置/人工登记恢复）
        Assert.False(sample.IsActive);
        Assert.Equal(SctTaskType.Standardize, _engine.Submitted.Single().TaskType);  // 仍自动入队，执行前强制前置测高
    }

    [Fact]
    public async Task 控样_初始厚度填非法值仍被拒()
    {
        var form = MakeSctForm(SampleType.SettingUp);
        form.InitialThickness = "-3";

        var error = await Create().RegisterAsync(form);

        Assert.Equal("初始厚度必须为大于 0 的数值", error);
        Assert.Empty(_samples.Store);
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

        var form = MakeProductionForm();
        form.RackPosition = "C-02";   // 换架位隔离编号重复断言（架位占用另有专门用例）
        var error = await service.RegisterAsync(form);

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

    // ==================== 检查样兼任类型标准化样（孪生档案） ====================

    [Fact]
    public async Task 检查样兼任_双档案互指同架位且合金只在孪生上()
    {
        var form = MakeSctForm(SampleType.Control, "HC-01", alloy: "6063");
        form.RegisterAsTypeStdTwin = true;

        var error = await Create().RegisterAsync(form);

        Assert.Null(error);
        Assert.Equal(2, _samples.Store.Count);
        var control = _samples.Store.Single(s => s.SampleId == "HC-01");
        var twin = _samples.Store.Single(s => s.SampleId == "HC-01-T");
        Assert.Equal(SampleType.Control, control.SampleType);
        Assert.Null(control.AlloyType);                 // 检查样本体不关联合金（触发器约束）
        Assert.Equal("HC-01-T", control.PairSampleId);
        Assert.Equal(SampleType.TypeStd, twin.SampleType);
        Assert.Equal("6063", twin.AlloyType);
        Assert.Equal("HC-01", twin.PairSampleId);
        Assert.Equal(control.RackPosition, twin.RackPosition);  // 同一物理样品同架位
        Assert.Equal(twin.MaxUsageCount, control.MaxUsageCount);
        Assert.Equal(0, twin.UsageCount);
        // 仅检查样入控制检查队列，孪生不主动入队（由合金切换/期限/生产前置触发）
        var submitted = Assert.Single(_engine.Submitted);
        Assert.Equal(SctTaskType.ControlCheck, submitted.TaskType);
        Assert.Equal("HC-01", submitted.SampleId);
    }

    [Fact]
    public async Task 检查样兼任_缺合金被拒()
    {
        var form = MakeSctForm(SampleType.Control, "HC-01");
        form.RegisterAsTypeStdTwin = true;

        var error = await Create().RegisterAsync(form);

        Assert.Equal("兼任类型标准化样必须填写合金", error);
        Assert.Empty(_samples.Store);
    }

    [Fact]
    public async Task 检查样兼任_孪生编号已存在被拒()
    {
        var service = Create();
        _samples.Store.Add(new SctSample
        {
            SampleId = "HC-01-T", SampleName = "既有孪生", SampleType = SampleType.TypeStd,
            AlloyType = "6063", RackPosition = "B-09", InitialThickness = 10, CurrentThickness = 10,
            MinThickness = 5, MaxUsageCount = 50, Status = SampleStatus.Normal,
        });

        var form = MakeSctForm(SampleType.Control, "HC-01", alloy: "6063");
        form.RackPosition = "B-02";
        form.RegisterAsTypeStdTwin = true;

        var error = await service.RegisterAsync(form);

        Assert.Equal("样品编号已存在：HC-01-T", error);
        Assert.Single(_samples.Store);                  // 既有样品未被覆盖
    }

    [Fact]
    public async Task 检查样不兼任_填合金仍被拒且不建孪生()
    {
        var error = await Create().RegisterAsync(MakeSctForm(SampleType.Control, "HC-01", alloy: "6063"));

        Assert.Equal("仅类型标准化样/生产样可关联合金，控样/检查样请留空合金", error);
        Assert.Empty(_samples.Store);
    }
}
