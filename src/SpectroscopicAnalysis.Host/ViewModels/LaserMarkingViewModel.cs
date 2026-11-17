using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpectroscopicAnalysis.Host.Vision;

namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>
/// 激光打标页 ViewModel：打标文本内容展示与编辑（输入框绑定）。
/// 文件读取经 ILaserMarkingService（MVVM 分层：VM 不做文件 I/O，不引用 WPF 类型）。
/// </summary>
public sealed partial class LaserMarkingViewModel : ObservableObject, IPageLifecycle
{
    private readonly ILaserMarkingService _laserMarkingService;

    /// <summary>打标文本内容（输入框显示；来源为配置路径的文本文件）。</summary>
    [ObservableProperty]
    private string _markingContent = string.Empty;

    /// <summary>内容文件路径（仅展示提示，供用户知晓来源）。</summary>
    [ObservableProperty]
    private string _contentFilePath = string.Empty;

    public LaserMarkingViewModel(ILaserMarkingService laserMarkingService)
    {
        _laserMarkingService = laserMarkingService;
    }

    /// <summary>进入页面时加载打标文本文件内容（导航生命周期）。</summary>
    public void OnNavigatedTo()
    {
        ReloadContent();
    }

    /// <summary>重新加载打标文本文件内容（用户手动刷新）。</summary>
    [RelayCommand]
    private void ReloadContent()
    {
        MarkingContent = _laserMarkingService.ReadMarkingContent();
    }
}
