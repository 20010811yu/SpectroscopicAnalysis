using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.SctStrategy;

namespace SpectroscopicAnalysis.Host.SampleManager;

/// <summary>
/// 样品注册服务实现（校验/查重规则与样品档案页注册面板一致，docs/开发规范.md L118 合金约束）；
/// I/O 在后台线程执行（Task.Run），失败经统一错误链上报（错误码 5003/5004）。
/// </summary>
public sealed class SampleRegistrationService : ISampleRegistrationService
{
    /// <summary>兼任孪生档案编号后缀（检查样 HC-01 → 孪生 HC-01-T；SamplesViewModel 内联注册同规则）。</summary>
    private const string TwinIdSuffix = "-T";

    private readonly ISctSampleRepository _samples;
    private readonly IProductionSampleRepository _production;
    private readonly ISctStrategyEngine _engine;
    private readonly IErrorCollector _errors;
    private readonly LoggingAlarm.ISystemLogCollector _systemLog;

    public SampleRegistrationService(
        ISctSampleRepository samples,
        IProductionSampleRepository production,
        ISctStrategyEngine engine,
        IErrorCollector errors,
        LoggingAlarm.ISystemLogCollector systemLog)
    {
        _samples = samples;
        _production = production;
        _engine = engine;
        _errors = errors;
        _systemLog = systemLog;
    }

    /// <inheritdoc />
    public async Task<string?> RegisterAsync(SampleRegisterForm form)
    {
        // 表单校验（同步纯逻辑，无需后台线程）：填写有误/未填写属用户输入问题，
        // 仅弹窗内红字提示（RegisterError），不进错误链（不写日志与报警面板）
        var error = ValidateForm(form);
        if (error is not null)
        {
            return error;
        }

        var type = form.SampleType;
        var sample = type == SampleType.Production ? null : BuildSample(form);
        var productionSample = type == SampleType.Production ? BuildProductionSample(form) : null;
        // 检查样兼任类型标准化样：同批构造 TypeStd 孪生档案（编号 -T 后缀、同架位、互指 PairSampleId；
        // 合金只落在孪生上——触发器约束合金仅 TypeStd 可关联）
        SctSample? twinSample = null;
        if (sample is not null && type == SampleType.Control && form.RegisterAsTypeStdTwin)
        {
            var twinId = sample.SampleId + TwinIdSuffix;
            twinSample = new SctSample
            {
                SampleId = twinId,
                SampleName = sample.SampleName + "（类型标准化）",
                SampleType = SampleType.TypeStd,
                AlloyType = string.IsNullOrWhiteSpace(form.AlloyType) ? null : form.AlloyType.Trim(),
                RackPosition = sample.RackPosition,
                InitialThickness = sample.InitialThickness,
                CurrentThickness = sample.CurrentThickness,
                MinThickness = sample.MinThickness,
                UsageCount = 0,
                MaxUsageCount = sample.MaxUsageCount,
                // 孪生随主样：未填初始厚度同样置需测高（首次使用前强制测高）
                Status = sample.Status,
                IsActive = sample.IsActive,
                CreatedTime = DateTime.Now,
                PairSampleId = sample.SampleId,
            };
            sample.AlloyType = null;    // 检查样本体不关联合金（触发器 trg_SCT_Sample_AlloyType_TypeStdOnly）
            sample.PairSampleId = twinId;
        }

        // 重复校验（按类型查各自表：生产样查 Production_Sample，其余查 SCT_Sample 编号/架位）
        var dupError = await Task.Run(() =>
        {
            var errors = new List<string>();
            if (productionSample is not null)
            {
                if (_production.GetAll().FirstOrDefault(p =>
                        string.Equals(p.SampleId, productionSample.SampleId, StringComparison.Ordinal)) is not null)
                {
                    errors.Add($"样品编号已存在：{productionSample.SampleId}");
                }
                // 架位占用查重（生产样独立 P01–P25 编号空间；防御性同时查 SCT 表与生产样表）
                var sctOccupant = _samples.GetAll().FirstOrDefault(s =>
                    string.Equals(s.RackPosition?.Trim(), productionSample.RackPosition, StringComparison.Ordinal));
                var prodOccupant = _production.GetAll().FirstOrDefault(p =>
                    string.Equals(p.RackPosition?.Trim(), productionSample.RackPosition, StringComparison.Ordinal));
                var occupant = sctOccupant is not null ? sctOccupant.SampleId : prodOccupant?.SampleId;
                if (occupant is not null)
                {
                    errors.Add($"货架位置已占用：{productionSample.RackPosition}（现有样品 {occupant}）");
                }
            }
            else if (sample is not null)
            {
                if (_samples.GetById(sample.SampleId) is not null)
                {
                    errors.Add($"样品编号已存在：{sample.SampleId}");
                }
                // 兼任孪生编号查重（-T 后缀）
                if (twinSample is not null && _samples.GetById(twinSample.SampleId) is not null)
                {
                    errors.Add($"样品编号已存在：{twinSample.SampleId}");
                }
                var occupant = _samples.GetAll().FirstOrDefault(s =>
                    string.Equals(s.RackPosition?.Trim(), sample.RackPosition, StringComparison.Ordinal));
                if (occupant is not null)
                {
                    errors.Add($"货架位置已占用：{sample.RackPosition}（现有样品 {occupant.SampleId}）");
                }
            }
            return errors.Count > 0 ? string.Join("\n", errors) : null;
        });
        if (dupError is not null)
        {
            // 编号/架位重复同属填写类错误：仅 UI 提示，不进错误链
            return dupError;
        }

        try
        {
            if (productionSample is not null)
            {
                await Task.Run(() => _production.Insert(productionSample));
            }
            else
            {
                await Task.Run(() =>
                {
                    _samples.Insert(sample!);
                    if (twinSample is not null)
                    {
                        _samples.Insert(twinSample);   // 兼任孪生同批入库（逐行事务，失败时上方统一进错误链）
                    }
                });
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _errors.Collect(ErrorCodes.SampleInsertFailed, "样品入库写库失败", form.SampleId.Trim(), ex);
            return $"入库失败：{ex.Message}";
        }

        // 注册完成自动加入任务队列：四类样品均入分析队列，任务类型按样品类型映射（引擎同目标同类型自动去重）
        var taskType = productionSample is not null
            ? SctTaskType.Production
            : type switch
            {
                SampleType.SettingUp => SctTaskType.Standardize,
                SampleType.Control => SctTaskType.ControlCheck,
                SampleType.TypeStd => SctTaskType.TypeStandardize,
                _ => SctTaskType.Production,
            };
        _engine.SubmitTask(taskType, form.SampleId.Trim(), "样品注册自动入队");
        _systemLog.Log("UI操作", twinSample is null
            ? $"样品注册：{form.SampleId.Trim()}（{form.SampleName}，{form.SampleType}）"
            : $"样品注册：{form.SampleId.Trim()}（{form.SampleName}，{form.SampleType}，兼任类型标准化样，孪生档案 {twinSample.SampleId}）");
        return null;
    }

    /// <summary>表单校验（按注册类型条件化）；返回错误消息，null 表示通过。</summary>
    private static string? ValidateForm(SampleRegisterForm form)
    {
        if (string.IsNullOrWhiteSpace(form.SampleId))
        {
            return "样品编号不能为空";
        }
        if (string.IsNullOrWhiteSpace(form.SampleName))
        {
            return "样品名称不能为空";
        }

        if (form.SampleType == SampleType.Production)
        {
            // 生产样：编号/名称 + 分析方法 + 合金类型 + 架位（必填，注册即确定分析样货架位置）
            if (string.IsNullOrWhiteSpace(form.AnalysisMethod))
            {
                return "请选择分析方法";
            }
            if (string.IsNullOrWhiteSpace(form.AlloyType))
            {
                return "请填写合金类型";
            }
            if (string.IsNullOrWhiteSpace(form.RackPosition))
            {
                return "样品架位置不能为空";
            }
            return null;
        }

        // 控样/检查样/类型标准样：架位 + 使用次数（初始厚度非必填：未填则注册后强制先测高）
        if (string.IsNullOrWhiteSpace(form.RackPosition))
        {
            return "样品架位置不能为空";
        }
        // 初始厚度：填了才校验（>0 且 > 最小厚度）；留空合法，注册后置需测高
        var hasInitial = double.TryParse(form.InitialThickness, out var init) && init > 0;
        if (!string.IsNullOrWhiteSpace(form.InitialThickness) && !hasInitial)
        {
            return "初始厚度必须为大于 0 的数值";
        }
        if (!double.TryParse(form.MinThickness, out var min) || min <= 0)
        {
            return "最小厚度必须为大于 0 的数值";
        }
        if (hasInitial && init <= min)
        {
            return "初始厚度必须大于最小厚度";
        }
        if (!int.TryParse(form.MaxUsageCount, out var maxUsage) || maxUsage < 1)
        {
            return "最大使用次数必须为不小于 1 的整数";
        }
        // 合金约束：类型标准化样必填合金（数据库触发器兜底，此处前置校验）；生产样合金已在上分支校验必填
        if (form.SampleType == SampleType.TypeStd && string.IsNullOrWhiteSpace(form.AlloyType))
        {
            return "类型标准化样必须填写关联合金";
        }
        // 检查样兼任类型标准化样：孪生档案需合金（数据库触发器兜底，此处前置校验）
        if (form.SampleType == SampleType.Control && form.RegisterAsTypeStdTwin && string.IsNullOrWhiteSpace(form.AlloyType))
        {
            return "兼任类型标准化样必须填写合金";
        }
        var allowAlloy = form.SampleType == SampleType.TypeStd
            || (form.SampleType == SampleType.Control && form.RegisterAsTypeStdTwin);
        if (!allowAlloy && form.SampleType != SampleType.Production && !string.IsNullOrWhiteSpace(form.AlloyType))
        {
            return "仅类型标准化样/生产样可关联合金，控样/检查样请留空合金";
        }
        return null;
    }

    /// <summary>生产样注册：按表单构造 ProductionSample（写 Production_Sample，无厚度/使用次数字段）。</summary>
    private static ProductionSample BuildProductionSample(SampleRegisterForm form) => new()
    {
        SampleId = form.SampleId.Trim(),
        SampleName = form.SampleName.Trim(),
        SampleType = SampleType.Production,
        AlloyType = form.AlloyType!.Trim(),             // 校验已确保非空
        AssociatedMethod = form.AnalysisMethod!.Trim(), // 校验已确保非空
        RackPosition = form.RackPosition?.Trim(),       // 校验已确保非空
        Status = ProductionStatus.Pending,
        CreatedTime = DateTime.Now,
    };

    /// <summary>
    /// SCT 样注册：入库初始化（UsageCount=0）。
    /// 初始厚度已填 → CurrentThickness=Initial、Status=Normal 直接可用；
    /// 未填 → 厚度未知存 0 哨兵（展示层显示 "—"）、Status=NeedMeasure 强制首次使用前测高（编排器前置测高/人工测高登记恢复）。
    /// </summary>
    private static SctSample BuildSample(SampleRegisterForm form)
    {
        // 初始厚度非必填：留空 = 厚度未知（0 哨兵），注册即需测高
        var hasInitial = double.TryParse(form.InitialThickness, out var init) && init > 0;
        return new SctSample
        {
            SampleId = form.SampleId.Trim(),
            SampleName = form.SampleName.Trim(),
            SampleType = form.SampleType,
            AlloyType = string.IsNullOrWhiteSpace(form.AlloyType) ? null : form.AlloyType.Trim(),
            RackPosition = form.RackPosition?.Trim(), // 校验已确保非空
            InitialThickness = hasInitial ? init : 0,
            CurrentThickness = hasInitial ? init : 0, // 已填：当前厚度 = 初始厚度；未填：0=厚度未知
            MinThickness = double.Parse(form.MinThickness),
            UsageCount = 0,
            MaxUsageCount = int.Parse(form.MaxUsageCount),
            Status = hasInitial ? SampleStatus.Normal : SampleStatus.NeedMeasure,
            IsActive = hasInitial,
            CreatedTime = DateTime.Now,
        };
    }
}
