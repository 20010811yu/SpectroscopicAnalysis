using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.Ui;

namespace SpectroscopicAnalysis.Host.ViewModels;

/// <summary>生产样品行展示模型（主控台「生产样品（今日）」模块）。</summary>
public sealed class ProductionSampleItem
{
    public required string SampleId { get; init; }
    public string? AlloyType { get; init; }
    public required string StatusText { get; init; }
}

/// <summary>
/// 主控台（生产监控）ViewModel：承载「生产样品（今日）」模块；
/// 数据库 I/O 经 Task.Run + IUiDispatcher 回 UI 线程（开发规范第 4 节）。
/// 其余面板随 D5+ 通信与 M2 引擎接入。
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject, IPageLifecycle
{
    private readonly ISctSampleRepository _samples;
    private readonly IUiDispatcher _ui;
    private readonly IErrorCollector _errors;

    /// <summary>生产样品行（今日入库），未筛选。</summary>
    private List<ProductionSampleItem> _allProduction = new();

    [ObservableProperty]
    private ObservableCollection<ProductionSampleItem> _productionSamples = new();

    /// <summary>是否存在生产样品记录（控制占位说明与表格的可见性）。</summary>
    [ObservableProperty]
    private bool _hasProduction;

    public DashboardViewModel(ISctSampleRepository samples, IUiDispatcher ui, IErrorCollector errors)
    {
        _samples = samples;
        _ui = ui;
        _errors = errors;
    }

    /// <summary>导航进入时加载（导航路径为 fire-and-forget；测试可 await LoadCommand.ExecuteAsync 保证确定性）。</summary>
    public void OnNavigatedTo() => LoadCommand.Execute(null);

    /// <summary>从数据库加载生产样品并刷新表格（I/O 在后台线程，结果经 IUiDispatcher 回 UI）。</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var rows = await Task.Run(() => _samples.GetAll());
            var production = rows
                .Where(r => r.SampleType == SampleType.Production)
                .Select(r => new ProductionSampleItem
                {
                    SampleId = r.SampleId,
                    AlloyType = r.AlloyType,
                    StatusText = r.Status switch
                    {
                        SampleStatus.Normal => "● 正常",
                        SampleStatus.NeedMeasure => "▲ 需测高",
                        SampleStatus.Warning => "▲ 超差预警",
                        SampleStatus.Disabled => "✖ 禁用（过薄）",
                        _ => r.Status,
                    },
                })
                .ToList();
            _ui.Invoke(() =>
            {
                _allProduction = production;
                ProductionSamples = new ObservableCollection<ProductionSampleItem>(production);
                HasProduction = production.Count > 0;
            });
        }
        catch (Exception ex)
        {
            // 加载失败经统一错误链上报（未捕获异常兜底码），不静默吞掉
            _errors.Collect(ErrorCodes.UnhandledException, "生产样品加载失败", null, ex);
        }
    }
}
