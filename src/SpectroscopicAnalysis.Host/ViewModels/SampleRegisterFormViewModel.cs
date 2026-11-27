using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.SampleManager;

namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>
/// 样品注册弹窗表单 ViewModel（主控台「样品注册」弹窗用；纯表单状态 + 确认/取消命令）。
/// 注册业务在 ISampleRegistrationService，经创建方注入的提交委托调用（VM 不依赖服务与窗口）。
/// 字段级校验在 VM 做即时提示（必填/数值范围，与注册服务规则一致）；查重类校验仍在服务层。
/// </summary>
public sealed partial class SampleRegisterFormViewModel : ObservableObject
{
    /// <summary>提交委托（创建方绑定注册服务）；返回错误提示，null 表示成功。</summary>
    private readonly Func<SampleRegisterFormViewModel, Task<string?>> _submitAsync;

    /// <summary>样品类型下拉选项（键 + 中文名成对，DisplayMemberPath 展示中文；SelectedValue 仍取英文键入库，生产样默认在前）。</summary>
    public IReadOnlyList<SampleTypeOption> SampleTypeOptions { get; } = new[]
    {
        new SampleTypeOption(Data.Models.SampleType.Production, Data.Models.SampleType.DisplayNames[Data.Models.SampleType.Production]),
        new SampleTypeOption(Data.Models.SampleType.SettingUp, Data.Models.SampleType.DisplayNames[Data.Models.SampleType.SettingUp]),
        new SampleTypeOption(Data.Models.SampleType.Control, Data.Models.SampleType.DisplayNames[Data.Models.SampleType.Control]),
        new SampleTypeOption(Data.Models.SampleType.TypeStd, Data.Models.SampleType.DisplayNames[Data.Models.SampleType.TypeStd]),
    };

    /// <summary>分析方法下拉选项（生产样注册用；与种子数据方法清单一致）。</summary>
    public IReadOnlyList<string> AnalysisMethodOptions { get; } = new[]
    {
        "GB/T 7999-A", "GB/T 7999-B", "ASTM E1251-A", "ASTM E1251-B",
    };

    [ObservableProperty]
    private string _sampleId = string.Empty;

    [ObservableProperty]
    private string _sampleName = string.Empty;

    [ObservableProperty]
    private string _sampleType = Data.Models.SampleType.Production;

    /// <summary>分析方法（生产样注册必选；其余类型不显示）。</summary>
    [ObservableProperty]
    private string? _analysisMethod;

    [ObservableProperty]
    private string? _alloyType;

    /// <summary>检查样兼任类型标准化样（勾选后同事务创建 TypeStd 孪生档案，编号 -T 后缀、同架位；合金必填）。</summary>
    [ObservableProperty]
    private bool _registerAsTypeStdTwin;

    /// <summary>SCT 样可用架位（A01–E05；打开弹窗前由调用方按当前占用填充，默认全布局兜底）。</summary>
    public IReadOnlyList<string> SctRackPositions { get; set; } =
        SampleManager.RackDirectory.AllPositions;

    /// <summary>生产样可用架位（P01–P25 独立编号空间，与 SCT 货架互不占用；打开弹窗前由调用方填充）。</summary>
    public IReadOnlyList<string> ProductionRackPositions { get; set; } =
        SampleManager.RackDirectory.ProductionAllPositions;

    /// <summary>当前类型的可用架位选项（生产样用 P 编号空间，SCT 样用 A01–E05；随类型切换刷新）。</summary>
    public IReadOnlyList<string> CurrentRackOptions =>
        SampleType == Data.Models.SampleType.Production ? ProductionRackPositions : SctRackPositions;

    /// <summary>SCT 货架已满提示（检查样/控样/类型标准化样且 A01–E05 无空闲架位时红字提示；生产样 P 空间独立不在此列）。</summary>
    public string? RackFullHint =>
        SampleType == Data.Models.SampleType.Production || SctRackPositions.Count > 0
            ? null
            : "SCT 货架已满（A01–E05 全部占用），请先清理架位后再注册";

    [ObservableProperty]
    private string? _rackPosition;

    partial void OnSampleTypeChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentRackOptions));
        OnPropertyChanged(nameof(RackFullHint));
        // 切类型后已选架位若不在新编号空间（A01–E05 ↔ P01–P25）则清空，避免跨空间残留
        if (!string.IsNullOrEmpty(RackPosition) && !CurrentRackOptions.Contains(RackPosition, StringComparer.Ordinal))
        {
            RackPosition = null;
        }
    }

    [ObservableProperty]
    private string _initialThickness = string.Empty;

    [ObservableProperty]
    private string _minThickness = string.Empty;

    [ObservableProperty]
    private string _maxUsageCount = string.Empty;

    // ---- 字段级校验错误（就地红字提示；用户修改该字段即清除）----

    [ObservableProperty]
    private string? _sampleIdError;

    [ObservableProperty]
    private string? _sampleNameError;

    [ObservableProperty]
    private string? _analysisMethodError;

    [ObservableProperty]
    private string? _rackPositionError;

    [ObservableProperty]
    private string? _thicknessError;

    [ObservableProperty]
    private string? _maxUsageCountError;

    [ObservableProperty]
    private string? _alloyTypeError;

    /// <summary>注册校验错误提示（查重/服务端错误汇总；空即无错误；就地红字显示）。</summary>
    [ObservableProperty]
    private string? _registerError;

    /// <summary>注册提交中标记（防重复提交；提交期间禁用确认按钮）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _isRegistering;

    /// <summary>注册结果（弹窗宿主据此关闭窗口：true=注册成功并关闭；false=取消关闭。
    /// 必须带变更通知——弹窗宿主监听 PropertyChanged 关窗，此前无通知导致确认/取消均无法关闭）。</summary>
    private bool _confirmed;

    public bool Confirmed
    {
        get => _confirmed;
        private set
        {
            // 不做同值守卫：取消（false→false）也必须发通知，弹窗宿主靠本事件关窗
            _confirmed = value;
            OnPropertyChanged();    // CommunityToolkit：按调用方成员名通知
        }
    }

    public SampleRegisterFormViewModel(Func<SampleRegisterFormViewModel, Task<string?>> submitAsync)
    {
        _submitAsync = submitAsync;
    }

    // 用户修改字段即清除对应错误提示（输入修正即时反馈）
    partial void OnSampleIdChanged(string value) => SampleIdError = null;
    partial void OnSampleNameChanged(string value) => SampleNameError = null;
    partial void OnAnalysisMethodChanged(string? value) => AnalysisMethodError = null;
    partial void OnRackPositionChanged(string? value) => RackPositionError = null;
    partial void OnInitialThicknessChanged(string value) => ThicknessError = null;
    partial void OnMinThicknessChanged(string value) => ThicknessError = null;
    partial void OnMaxUsageCountChanged(string value) => MaxUsageCountError = null;
    partial void OnAlloyTypeChanged(string? value) => AlloyTypeError = null;

    /// <summary>确认入库：先字段级校验（全部通过才提交），再调提交委托；失败就地显示错误留在弹窗，成功置 Confirmed。</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (IsRegistering)
        {
            return;
        }

        RegisterError = null;
        if (!ValidateFields())
        {
            return;     // 字段级错误已就地提示，不提交
        }

        IsRegistering = true;
        try
        {
            var error = await _submitAsync(this);
            if (error is not null)
            {
                RegisterError = error;
                return;
            }

            Confirmed = true;
        }
        finally
        {
            IsRegistering = false;
        }
    }

    private bool CanConfirm() => !IsRegistering;

    /// <summary>取消注册（弹窗宿主据此关闭窗口，不做任何写入）。</summary>
    [RelayCommand]
    private void Cancel() => Confirmed = false;

    /// <summary>字段级校验（与注册服务 ValidateForm 规则一致，前置于提交即时反馈）；返回是否全部通过。</summary>
    private bool ValidateFields()
    {
        var valid = true;

        if (string.IsNullOrWhiteSpace(SampleId))
        {
            SampleIdError = "请填写样品编号";
            valid = false;
        }

        if (string.IsNullOrWhiteSpace(SampleName))
        {
            SampleNameError = "请填写样品名称";
            valid = false;
        }

        if (SampleType == Data.Models.SampleType.Production)
        {
            // 生产样：分析方法必选 + 合金类型必填 + 架位必选（服务端同规则，注册即确定分析样货架位置）
            if (string.IsNullOrWhiteSpace(AnalysisMethod))
            {
                AnalysisMethodError = "请选择分析方法";
                valid = false;
            }
            if (string.IsNullOrWhiteSpace(AlloyType))
            {
                AlloyTypeError = "请填写合金类型";
                valid = false;
            }
            if (string.IsNullOrWhiteSpace(RackPosition))
            {
                RackPositionError = "请选择样品架位置";
                valid = false;
            }

            return valid;
        }

        // SCT 样（标准化样/检查样/类型标准化样）：架位、厚度（初始厚度非必填）、使用次数
        if (string.IsNullOrWhiteSpace(RackPosition))
        {
            RackPositionError = "请填写样品架位置";
            valid = false;
        }

        // 初始厚度：留空合法（注册后强制先测高）；填了才校验 >0 且 > 最小厚度
        var hasMin = double.TryParse(MinThickness, out var min);
        var initialBlank = string.IsNullOrWhiteSpace(InitialThickness);
        var hasInitial = double.TryParse(InitialThickness, out var initial);
        if (!initialBlank && (!hasInitial || initial <= 0))
        {
            ThicknessError = "初始厚度必须为大于 0 的数值";
            valid = false;
        }
        else if (!hasMin || min <= 0)
        {
            ThicknessError = "最小厚度必须为大于 0 的数值";
            valid = false;
        }
        else if (hasInitial && initial <= min)
        {
            ThicknessError = "初始厚度必须大于最小厚度";
            valid = false;
        }

        if (!int.TryParse(MaxUsageCount, out var usage) || usage < 1)
        {
            MaxUsageCountError = "最大使用次数必须为不小于 1 的整数";
            valid = false;
        }

        if (SampleType == Data.Models.SampleType.TypeStd && string.IsNullOrWhiteSpace(AlloyType))
        {
            AlloyTypeError = "类型标准化样必须关联合金";
            valid = false;
        }

        // 检查样兼任类型标准化样：孪生档案需合金（服务端同规则）
        if (SampleType == Data.Models.SampleType.Control && RegisterAsTypeStdTwin && string.IsNullOrWhiteSpace(AlloyType))
        {
            AlloyTypeError = "兼任类型标准化样必须填写合金";
            valid = false;
        }

        return valid;
    }

    /// <summary>转换为注册表单数据（供注册服务消费；字符串保留原始输入）。</summary>
    public SampleRegisterForm ToForm() => new()
    {
        SampleType = SampleType,
        SampleId = SampleId,
        SampleName = SampleName,
        AnalysisMethod = AnalysisMethod,
        AlloyType = AlloyType,
        RegisterAsTypeStdTwin = RegisterAsTypeStdTwin,
        RackPosition = RackPosition,
        InitialThickness = InitialThickness,
        MinThickness = MinThickness,
        MaxUsageCount = MaxUsageCount,
    };
}
