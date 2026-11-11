using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SpectroscopicAnalysis.Host.Views;

/// <summary>主控台页（InitializeComponent + 纯视觉行为：表格滚轮转发，业务在 ViewModel——开发规范 3.3）。</summary>
public partial class DashboardPage : UserControl
{
    public DashboardPage() => InitializeComponent();

    /// <summary>
    /// 生产样品表格滚轮转发：DataGrid 内部 ScrollViewer 即使无可滚动内容也会把滚轮事件标记为已处理，
    /// 导致外层页面 ScrollViewer 收不到、页面无法滚动。在隧道阶段捕获后在父元素重新引发同参事件，
    /// 交由整页 ScrollViewer 处理（表格每页 ≤10 行、高度随内容自适应，页面级滚动即唯一滚动需求）。
    /// </summary>
    private void ProductionGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        };
        ((UIElement)((Control)sender).Parent).RaiseEvent(forwarded);
    }
}
