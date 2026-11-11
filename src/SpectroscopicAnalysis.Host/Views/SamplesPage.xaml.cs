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

    /// <summary>缩放分档阈值：≤125% 时操作列固定最右（列宽 Star 模式），>125% 切 Auto 模式。</summary>
    private const double CompactScaleThreshold = 1.25;

    /// <summary>列宽 Star 模式备份（切到 >125% 的 Auto 模式前保存，切回时恢复）。</summary>
    private readonly Dictionary<DataGridColumn, DataGridLength> _starWidths = new();

    /// <summary>当前列布局模式（true = Star 紧凑模式）。</summary>
    private bool? _compactMode;

    public SamplesPage() => InitializeComponent();

    /// <summary>
    /// 每次布局后：① 把台账可用高度均分为每页 15 行（表头除外），保证恰好显示 15 行数据；
    /// ② 按窗口缩放档位切换列布局（≤125% 列宽 Star 自适应、操作列固定最右；>125% 列宽 Auto、横向滚动）。
    /// 均为纯视觉行为，不涉及业务。值不变时跳过，避免触发布局死循环。
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

        ApplyColumnMode();
    }

    /// <summary>读取主窗口根布局缩放档位（MainWindow 根 Grid 的 ScaleTransform）。</summary>
    private double GetUiScale()
    {
        var transform = Window.GetWindow(this)?.LayoutTransform as ScaleTransform;
        return transform?.ScaleX ?? 1.0;
    }

    /// <summary>按缩放档位切换列宽模式；模式未变化时跳过。</summary>
    private void ApplyColumnMode()
    {
        var compact = GetUiScale() <= CompactScaleThreshold + 0.001;
        if (_compactMode == compact)
        {
            return;
        }

        _compactMode = compact;
        if (compact)
        {
            // Star 模式：列宽按比例随窗口伸缩，操作列（固定宽）始终靠在最右侧
            foreach (var column in LedgerGrid.Columns)
            {
                if (_starWidths.TryGetValue(column, out var width))
                {
                    column.Width = width;
                }
            }
        }
        else
        {
            // Auto 模式：列宽按内容自然宽度，超出视口时表格横向滚动
            _starWidths.Clear();
            foreach (var column in LedgerGrid.Columns)
            {
                if (!column.Width.IsStar)
                {
                    continue; // 操作列等固定宽度列保持不变
                }

                _starWidths[column] = column.Width;
                column.Width = DataGridLength.Auto;
            }
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
