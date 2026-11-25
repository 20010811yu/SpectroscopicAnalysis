using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>样品注册弹窗表单 ViewModel 测试：类型选项中文化映射 + 字段级校验即时提示。</summary>
public sealed class SampleRegisterFormViewModelTests
{
    private static SampleRegisterFormViewModel CreateVm() =>
        new(_ => Task.FromResult<string?>("服务端错误"));   // 提交委托默认返回错误，聚焦字段级校验

    [Fact]
    public void SampleTypeOptions_选项含中文名且键与常量一致()
    {
        var vm = CreateVm();

        Assert.Equal(4, vm.SampleTypeOptions.Count);
        Assert.Equal(SampleType.Production, vm.SampleTypeOptions[0].Key);       // 生产样默认在前
        Assert.Equal("生产样品", vm.SampleTypeOptions[0].Display);
        Assert.Equal(SampleType.SettingUp, vm.SampleTypeOptions[1].Key);
        Assert.Equal("标准化样", vm.SampleTypeOptions[1].Display);
        // SelectedValue 键仍为英文常量（入库不变）
        Assert.Equal(SampleType.Production, vm.SampleType);
    }

    [Fact]
    public async Task Confirm_生产样缺分析方法_就地提示且不提交()
    {
        var submitted = false;
        var vm = new SampleRegisterFormViewModel(_ =>
        {
            submitted = true;
            return Task.FromResult<string?>(null);
        });
        vm.SampleId = "P-1";
        vm.SampleName = "样品";

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.False(submitted);                    // 字段级校验拦截，未走提交
        Assert.Equal("请选择分析方法", vm.AnalysisMethodError);
        Assert.Null(vm.RegisterError);
        Assert.False(vm.Confirmed);
    }

    [Fact]
    public async Task Confirm_SCT样厚度非法_就地提示且不提交()
    {
        var vm = CreateVm();
        vm.SampleType = SampleType.SettingUp;
        vm.SampleId = "S-1";
        vm.SampleName = "标样";
        vm.RackPosition = "A-01";
        vm.InitialThickness = "5";
        vm.MinThickness = "8";                      // 初始 <= 最小 → 拦截
        vm.MaxUsageCount = "10";

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("初始厚度必须大于最小厚度", vm.ThicknessError);
        Assert.Null(vm.MaxUsageCountError);
    }

    [Fact]
    public async Task Confirm_SCT样使用次数非整数_就地提示且不提交()
    {
        var vm = CreateVm();
        vm.SampleType = SampleType.Control;
        vm.SampleId = "S-2";
        vm.SampleName = "控样";
        vm.RackPosition = "A-02";
        vm.InitialThickness = "8";
        vm.MinThickness = "5";
        vm.MaxUsageCount = "abc";

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("最大使用次数必须为不小于 1 的整数", vm.MaxUsageCountError);
    }

    [Fact]
    public async Task Confirm_TypeStd缺合金_就地提示且不提交()
    {
        var vm = CreateVm();
        vm.SampleType = SampleType.TypeStd;
        vm.SampleId = "T-1";
        vm.SampleName = "类型标样";
        vm.RackPosition = "A-03";
        vm.InitialThickness = "8";
        vm.MinThickness = "5";
        vm.MaxUsageCount = "3";

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("类型标准化样必须关联合金", vm.AlloyTypeError);
    }

    [Fact]
    public async Task Confirm_字段全通过_提交服务并回填服务端错误()
    {
        var vm = CreateVm();
        vm.SampleType = SampleType.SettingUp;
        vm.SampleId = "S-3";
        vm.SampleName = "标样";
        vm.RackPosition = "A-04";
        vm.InitialThickness = "8";
        vm.MinThickness = "5";
        vm.MaxUsageCount = "3";

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Null(vm.SampleIdError);              // 字段级校验全部通过
        Assert.Null(vm.RackPositionError);
        Assert.Equal("服务端错误", vm.RegisterError);   // 服务端错误（如查重）回填汇总区
        Assert.False(vm.Confirmed);
    }

    [Fact]
    public async Task Confirm_字段与服务端全部通过_置Confirmed()
    {
        var vm = new SampleRegisterFormViewModel(_ => Task.FromResult<string?>(null));
        vm.SampleId = "P-2";
        vm.SampleName = "生产样";
        vm.AnalysisMethod = "GB/T 7999-A";
        vm.AlloyType = "3003";   // 生产样合金必填

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.True(vm.Confirmed);
    }

    [Fact]
    public void 修改字段即清除对应错误提示()
    {
        var vm = CreateVm();
        vm.SampleIdError = "请填写样品编号";
        vm.ThicknessError = "初始厚度必须大于 0 的数值";

        vm.SampleId = "P-9";
        vm.InitialThickness = "10";

        Assert.Null(vm.SampleIdError);
        Assert.Null(vm.ThicknessError);
    }

    [Fact]
    public async Task Confirmed_成功与取消均触发变更通知_弹窗宿主据此关窗()
    {
        var changes = new List<string?>();
        var vm = new SampleRegisterFormViewModel(_ => Task.FromResult<string?>(null));
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        vm.SampleId = "P-3";
        vm.SampleName = "生产样";
        vm.AnalysisMethod = "GB/T 7999-A";
        vm.AlloyType = "3003";   // 生产样合金必填
        await vm.ConfirmCommand.ExecuteAsync(null);     // 成功 → Confirmed=true 通知
        vm.CancelCommand.Execute(null);                 // 取消 → Confirmed=false 通知

        Assert.Contains(nameof(SampleRegisterFormViewModel.Confirmed), changes);
        Assert.Equal(2, changes.Count(c => c == nameof(SampleRegisterFormViewModel.Confirmed)));
    }
}
