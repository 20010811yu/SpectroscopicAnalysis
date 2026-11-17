using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpectroscopicAnalysis.Host.Vision;

namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>
/// 激光打标页 ViewModel：打标文本内容展示与编辑（输入框绑定，修改即写回文本文件）。
/// 文件读写经 ILaserMarkingService（MVVM 分层：VM 不做文件 I/O，不引用 WPF 类型）。
/// </summary>
public sealed partial class LaserMarkingViewModel : ObservableObject, IPageLifecycle
{
    private readonly ILaserMarkingService _laserMarkingService;

    /// <summary>加载/回写过程中的回填标志：回填触发的属性变更不写回文件，避免无意义落盘与循环覆盖。</summary>
    private bool _isLoading;

    /// <summary>打标文本内容（输入框显示与编辑；修改后立即写回配置路径的文本文件）。</summary>
    [ObservableProperty]
    private string _markingContent = string.Empty;

    /// <summary>内容文件路径（仅展示提示，供用户知晓来源）。</summary>
    [ObservableProperty]
    private string _contentFilePath = string.Empty;

    /// <summary>最近一次写回结果提示（成功/失败，供页面状态栏展示）。</summary>
    [ObservableProperty]
    private string _saveStatus = string.Empty;

    public LaserMarkingViewModel(ILaserMarkingService laserMarkingService)
    {
        _laserMarkingService = laserMarkingService;
    }

    /// <summary>进入页面时加载打标文本文件内容（导航生命周期）。</summary>
    public void OnNavigatedTo()
    {
        ReloadContent();
    }

    /// <summary>重新加载打标文本文件内容（用户手动刷新；加载期间的回填不触发写回）。</summary>
    [RelayCommand]
    private void ReloadContent()
    {
        _isLoading = true;
        MarkingContent = _laserMarkingService.ReadMarkingContent();
        _isLoading = false;
        SaveStatus = string.Empty;
    }

    /// <summary>输入框内容变更（用户编辑）：立即写回文本文件。</summary>
    partial void OnMarkingContentChanged(string value)
    {
        if (_isLoading)
        {
            return;
        }

        // 编辑内容写回 txt（服务层原子替换写入）；结果状态提示由页面展示
        SaveStatus = _laserMarkingService.WriteMarkingContent(value) ? "已保存" : "保存失败";
    }
}
