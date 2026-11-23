using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpectroscopicAnalysis.Host.Vision;
using System.Collections.Generic;

namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>
/// 激光打标页 ViewModel：打标文本内容展示与编辑（输入框绑定，修改即写回文本文件）。
/// 文件读写经 ILaserMarkingService（MVVM 分层：VM 不做文件 I/O，不引用 WPF 类型）。
/// </summary>
public sealed partial class LaserMarkingViewModel : ObservableObject, IPageLifecycle
{
    private readonly ILaserMarkingService _laserMarkingService;

    /// <summary>EzCad2 在线状态（进程存活=在线；与顶栏状态点同源 IEzCad2Controller）。
    /// OnlineStateChanged 可在任意线程触发，经 IUiDispatcher 切回 UI 线程（开发规范第 4 节）。</summary>
    [ObservableProperty]
    private bool _isOnline;

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

    /// <summary>打标方式候选（文本打印=2.ezd / 条码打印=1.ezd，路径映射在服务层）。须为实例公共属性——WPF {Binding} 无法绑定静态字段。</summary>
    public IReadOnlyList<string> MarkingModes { get; } = ["文本打印", "条码打印"];

    /// <summary>当前打标方式（默认文本打印；切换时联动 EzCad2 隐藏式重新加载对应工程文件）。</summary>
    [ObservableProperty]
    private string _markingMode = "文本打印";

    /// <summary>打标方式切换执行中标志（防重入）。</summary>
    private bool _isSwitchingMode;

    public LaserMarkingViewModel(ILaserMarkingService laserMarkingService,
        IEzCad2Controller ezCad2, Ui.IUiDispatcher ui)
    {
        _laserMarkingService = laserMarkingService;
        IsOnline = ezCad2.IsOnline;
        ezCad2.OnlineStateChanged += () => ui.Invoke(() => IsOnline = ezCad2.IsOnline);
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

        var oldContent = _laserMarkingService.ReadMarkingContent();
        // 编辑内容写回 txt（服务层原子替换写入）；结果状态提示由页面展示
        var saved = _laserMarkingService.WriteMarkingContent(value);
        SaveStatus = saved ? "已保存" : "保存失败";
        if (saved)
        {
            _laserMarkingService.LogContentChanged(oldContent, value);   // 全流程日志：打印内容变更
        }
    }

    /// <summary>打标方式切换（用户下拉选择）：联动 EzCad2 隐藏式重新加载对应工程文件。</summary>
    partial void OnMarkingModeChanged(string value)
    {
        if (_isSwitchingMode)
        {
            return;
        }

        _isSwitchingMode = true;
        SaveStatus = $"正在切换为{value}…";
        _ = SwitchModeAsync(MarkingMode, value);
    }

    private async Task SwitchModeAsync(string oldMode, string newMode)
    {
        try
        {
            var error = await _laserMarkingService.SwitchMarkingModeAsync(newMode);
            SaveStatus = error is null ? $"已切换为{newMode}" : $"切换失败：{error}";
            _laserMarkingService.LogModeChanged(oldMode, newMode, error is null, error);   // 全流程日志：打印方式变更
        }
        finally
        {
            _isSwitchingMode = false;
        }
    }
}
