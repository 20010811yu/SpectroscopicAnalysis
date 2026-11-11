using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Host.Views;

/// <summary>样品档案页（InitializeComponent + 纯视觉行为：按视口高度反算台账行高）。</summary>
public partial class SamplesPage : UserControl
{
    /// <summary>台账行高下限（窗口过矮时兜底，此时表格内部滚动条自动接手）。</summary>
    private const double MinRowHeight = 22.0;

    public SamplesPage() => InitializeComponent();

    /// <summary>
    /// 每次布局后把台账可用高度均分为每页 15 行（表头除外），保证恰好显示 15 行数据、
    /// 无需表格内滚动（纯视觉行为，不涉及业务）。值不变时跳过，避免触发布局死循环。
    /// </summary>
    private void LedGrid_LayoutUpdated(object? sender, EventArgs e)
    {
        if (LedgerGrid is not DataGrid dg || !dg.IsLoaded || dg.ActualHeight <= 0)
        {
            return;
        }

        var headerHeight = FindColumnHeadersPresenter(dg)?.ActualHeight ?? 0;
        if (headerHeight <= 0)
        {
            return;
        }

        var rowHeight = Math.Floor((dg.ActualHeight - headerHeight) / SamplesViewModel.PageSize);
        var clamped = Math.Max(MinRowHeight, rowHeight);
        if (!double.IsNaN(clamped) && !AreClose(dg.RowHeight, clamped))
        {
            dg.RowHeight = clamped;
        }
    }

    private static bool AreClose(double a, double b) => Math.Abs(a - b) < 0.5;

    /// <summary>在可视树中查找列头呈现器（取其当前高度参与行高计算）。</summary>
    private static DataGridColumnHeadersPresenter? FindColumnHeadersPresenter(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is DataGridColumnHeadersPresenter presenter)
            {
                return presenter;
            }

            var found = FindColumnHeadersPresenter(child);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }
}
