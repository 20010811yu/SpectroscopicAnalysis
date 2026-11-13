using System.Windows; // MessageBoxButton/MessageBoxImage/MessageBoxResult（System.Windows 枚举，仅实现层引用）
using HandyControl.Controls;

namespace SpectroscopicAnalysis.Host.Ui;

/// <summary>确认弹窗实现：HandyControl MessageBox（危险操作二次确认用）。</summary>
public sealed class DialogService : IDialogService
{
    /// <inheritdoc />
    public bool Confirm(string title, string message)
    {
        var result = HandyControl.Controls.MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        return result == MessageBoxResult.OK;
    }

    /// <inheritdoc />
    public void Alert(string title, string message)
    {
        HandyControl.Controls.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
