using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.SampleManager;

namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>
/// 样品注册弹窗表单 ViewModel（主控台「样品注册」弹窗用；纯表单状态 + 确认/取消命令）。
/// 注册业务在 ISampleRegistrationService，经创建方注入的提交委托调用（VM 不依赖服务与窗口）。
/// </summary>
public sealed partial class SampleRegisterFormViewModel : ObservableObject
{
    /// <summary>提交委托（创建方绑定注册服务）；返回错误提示，null 表示成功。</summary>
    private readonly Func<SampleRegisterFormViewModel, Task<string?>> _submitAsync;

    /// <summary>样品类型下拉选项（类型键：生产样默认在前；控样/检查样/类型标准化样）。</summary>
    public IReadOnlyList<string> SampleTypeOptions { get; } = new[]
    {
        Data.Models.SampleType.Production, Data.Models.SampleType.SettingUp,
        Data.Models.SampleType.Control, Data.Models.SampleType.TypeStd,
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

    [ObservableProperty]
    private string? _rackPosition;

    [ObservableProperty]
    private string _initialThickness = string.Empty;

    [ObservableProperty]
    private string _minThickness = string.Empty;

    [ObservableProperty]
    private string _maxUsageCount = string.Empty;

    /// <summary>注册校验错误提示（空即无错误；就地红字显示）。</summary>
    [ObservableProperty]
    private string? _registerError;

    /// <summary>注册提交中标记（防重复提交；提交期间禁用确认按钮）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _isRegistering;

    /// <summary>注册成功（弹窗宿主据此关闭窗口并刷新）。</summary>
    public bool Confirmed { get; private set; }

    public SampleRegisterFormViewModel(Func<SampleRegisterFormViewModel, Task<string?>> submitAsync)
    {
        _submitAsync = submitAsync;
    }

    /// <summary>确认入库：表单数据 → 提交委托（注册服务）；失败就地显示错误留在弹窗，成功置 Confirmed。</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (IsRegistering)
        {
            return;
        }

        IsRegistering = true;
        RegisterError = null;
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

    /// <summary>转换为注册表单数据（供注册服务消费；字符串保留原始输入）。</summary>
    public SampleRegisterForm ToForm() => new()
    {
        SampleType = SampleType,
        SampleId = SampleId,
        SampleName = SampleName,
        AnalysisMethod = AnalysisMethod,
        AlloyType = AlloyType,
        RackPosition = RackPosition,
        InitialThickness = InitialThickness,
        MinThickness = MinThickness,
        MaxUsageCount = MaxUsageCount,
    };
}
