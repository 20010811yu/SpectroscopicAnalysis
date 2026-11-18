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
    private readonly ISctSampleRepository _samples;
    private readonly IProductionSampleRepository _production;
    private readonly ISctStrategyEngine _engine;
    private readonly IErrorCollector _errors;

    public SampleRegistrationService(
        ISctSampleRepository samples,
        IProductionSampleRepository production,
        ISctStrategyEngine engine,
        IErrorCollector errors)
    {
        _samples = samples;
        _production = production;
        _engine = engine;
        _errors = errors;
    }

    /// <inheritdoc />
    public async Task<string?> RegisterAsync(SampleRegisterForm form)
    {
        // 表单校验（同步纯逻辑，无需后台线程）
        var error = ValidateForm(form);
        if (error is not null)
        {
            _errors.Collect(ErrorCodes.SampleRegisterRejected, error, form.SampleId.Trim());
            return error;
        }

        var type = form.SampleType;
        var sample = type == SampleType.Production ? null : BuildSample(form);
        var productionSample = type == SampleType.Production ? BuildProductionSample(form) : null;

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
            }
            else if (sample is not null)
            {
                if (_samples.GetById(sample.SampleId) is not null)
                {
                    errors.Add($"样品编号已存在：{sample.SampleId}");
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
            _errors.Collect(ErrorCodes.SampleRegisterRejected, dupError, form.SampleId.Trim());
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
                await Task.Run(() => _samples.Insert(sample!));
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
            // 生产样：编号/名称 + 分析方法
            if (string.IsNullOrWhiteSpace(form.AnalysisMethod))
            {
                return "请选择分析方法";
            }
            return null;
        }

        // 设置样/控制样/类型标准样：架位 + 厚度 + 使用次数
        if (string.IsNullOrWhiteSpace(form.RackPosition))
        {
            return "样品架位置不能为空";
        }
        if (!double.TryParse(form.InitialThickness, out var init) || init <= 0)
        {
            return "初始厚度必须为大于 0 的数值";
        }
        if (!double.TryParse(form.MinThickness, out var min) || min <= 0)
        {
            return "最小厚度必须为大于 0 的数值";
        }
        if (init <= min)
        {
            return "初始厚度必须大于最小厚度";
        }
        if (!int.TryParse(form.MaxUsageCount, out var maxUsage) || maxUsage < 1)
        {
            return "最大使用次数必须为不小于 1 的整数";
        }
        // 合金约束：仅类型标准化样必填合金（数据库触发器兜底，此处前置校验）
        if (form.SampleType == SampleType.TypeStd && string.IsNullOrWhiteSpace(form.AlloyType))
        {
            return "类型标准化样必须填写关联合金";
        }
        if (form.SampleType != SampleType.TypeStd && !string.IsNullOrWhiteSpace(form.AlloyType))
        {
            return "仅类型标准化样可关联合金，设置样/控制样请留空合金";
        }
        return null;
    }

    /// <summary>生产样注册：按表单构造 ProductionSample（写 Production_Sample，无厚度/使用次数字段）。</summary>
    private static ProductionSample BuildProductionSample(SampleRegisterForm form) => new()
    {
        SampleId = form.SampleId.Trim(),
        SampleName = form.SampleName.Trim(),
        SampleType = SampleType.Production,
        AssociatedMethod = form.AnalysisMethod!.Trim(), // 校验已确保非空
        Status = ProductionStatus.Pending,
        CreatedTime = DateTime.Now,
    };

    /// <summary>SCT 样注册：入库初始化（CurrentThickness=Initial、UsageCount=0、Status=Normal）。</summary>
    private static SctSample BuildSample(SampleRegisterForm form) => new()
    {
        SampleId = form.SampleId.Trim(),
        SampleName = form.SampleName.Trim(),
        SampleType = form.SampleType,
        AlloyType = string.IsNullOrWhiteSpace(form.AlloyType) ? null : form.AlloyType.Trim(),
        RackPosition = form.RackPosition?.Trim(), // 校验已确保非空
        InitialThickness = double.Parse(form.InitialThickness),
        CurrentThickness = double.Parse(form.InitialThickness), // 入库初始化：当前厚度 = 初始厚度
        MinThickness = double.Parse(form.MinThickness),
        UsageCount = 0,
        MaxUsageCount = int.Parse(form.MaxUsageCount),
        Status = SampleStatus.Normal,
        IsActive = true,
        CreatedTime = DateTime.Now,
    };
}
