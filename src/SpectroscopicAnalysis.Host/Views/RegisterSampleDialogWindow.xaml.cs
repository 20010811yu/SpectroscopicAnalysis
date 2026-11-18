using System.ComponentModel;
using System.Windows;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Host.Views;

/// <summary>样品注册弹窗（InitializeComponent + 纯视觉行为：注册成功后关闭窗口，业务在 ViewModel/注册服务——开发规范 3.3）。</summary>
public partial class RegisterSampleDialogWindow : Window
{
    public RegisterSampleDialogWindow()
    {
        InitializeComponent();
        // 表单 VM 的 Confirmed=true（注册成功）后关闭窗口（纯视觉关闭行为，不承载业务）
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SampleRegisterFormViewModel vm)
            {
                vm.PropertyChanged += VmOnPropertyChanged;
            }
        };
    }

    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SampleRegisterFormViewModel.Confirmed)
            && sender is SampleRegisterFormViewModel { Confirmed: true })
        {
            Close();
        }
    }
}
