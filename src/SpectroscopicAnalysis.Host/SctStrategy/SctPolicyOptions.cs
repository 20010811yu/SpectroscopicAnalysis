using System.IO;
using System.Text.Json;
using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.SctStrategy;

/// <summary>控制样插入模式（任务清单第 5 节③：按分析数量 / 按时间间隔）。</summary>
public static class ControlInsertMode
{
    public const string ByCount = "ByCount";
    public const string ByTime = "ByTime";
}

/// <summary>设置样选择规则（任务清单第 5 节②：多个可用时选使用次数最少 / 轮询）。</summary>
public static class SettingSampleSelectionMode
{
    /// <summary>选使用次数最少的设置样。</summary>
    public const string LeastUsed = "LeastUsed";

    /// <summary>轮询选取。</summary>
    public const string RoundRobin = "RoundRobin";
}

/// <summary>
/// SCT 策略配置（PDF 第六节 + 任务清单第 5 节：控制样插入、超差判定、标准化触发项、
/// 设置样选择规则、控制样轮换与合金匹配、类型标准化触发、超差闭环重测上限）。
/// 纯数据对象，不含策略逻辑；持久化经 ISctPolicyStore。
/// </summary>
public sealed class SctPolicyOptions
{
    /// <summary>设置样选择规则：LeastUsed / RoundRobin（任务清单第 5 节②）。</summary>
    public string SettingSampleSelectionMode { get; set; } = "LeastUsed";

    /// <summary>多块控制样轮换启用（任务清单第 5 节③）。</summary>
    public bool ControlRotationEnabled { get; set; } = true;

    /// <summary>合金类型切换时触发类型标准化（任务清单第 5 节⑤）。</summary>
    public bool TypeStandardizeOnAlloySwitch { get; set; } = true;

    /// <summary>类型标准化期限届满触发开关（任务清单第 5 节⑤）。</summary>
    public bool TypeStandardizePeriodEnabled { get; set; } = false;

    /// <summary>类型标准化期限（天）。</summary>
    public int TypeStandardizePeriodDays { get; set; } = 7;

    /// <summary>控制样超差闭环：标准化后验证重测次数上限（仍失败 → 报警并暂停自动模式，任务清单第 5 节④）。</summary>
    public int ControlRecheckMaxCount { get; set; } = 1;

    /// <summary>控制样插入模式：ByCount / ByTime。</summary>
    public string ControlInsertMode { get; set; } = "ByCount";

    /// <summary>按数量插入：每 N 个生产样品插入一次控制样。</summary>
    public int ControlInsertCount { get; set; } = 15;

    /// <summary>按时间插入：间隔分钟数。</summary>
    public int ControlInsertMinutes { get; set; } = 60;

    /// <summary>控制样判定限类型：Absolute / Percent / Sigma（ControlLimit.LimitType 同名）。</summary>
    public string LimitType { get; set; } = "Sigma";

    /// <summary>判定限值（如 σ 倍数 2/3，或百分比 5 表示 ±5%）。</summary>
    public double LimitValue { get; set; } = 2;

    /// <summary>控制样超差后自动触发设置样标准化（超差闭环第一步）。</summary>
    public bool AutoStandardizeOnControlFail { get; set; } = true;

    /// <summary>切换到自动模式时执行标准化。</summary>
    public bool StandardizeOnAutoMode { get; set; } = true;

    /// <summary>启用定时标准化。</summary>
    public bool PeriodicStandardizeEnabled { get; set; } = false;

    /// <summary>定时标准化间隔（小时）。</summary>
    public int PeriodicStandardizeHours { get; set; } = 8;
}

/// <summary>SCT 策略配置存取接口（仅配置持久化，不含策略执行逻辑）。</summary>
public interface ISctPolicyStore
{
    /// <summary>读取配置；文件不存在或损坏时返回默认值。</summary>
    SctPolicyOptions Load();

    /// <summary>保存配置（覆盖写入）。</summary>
    void Save(SctPolicyOptions options);
}

/// <summary>SCT 策略配置 JSON 存储（config/sct_policy.json；密钥/连接串不入库，本文件仅策略参数）。</summary>
public sealed class JsonSctPolicyStore : ISctPolicyStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    /// <summary>默认写到程序目录下 config/sct_policy.json（与代码分离，目录约定见 AGENTS.md）。</summary>
    public JsonSctPolicyStore()
        : this(Path.Combine(AppContext.BaseDirectory, "config", "sct_policy.json"))
    {
    }

    /// <summary>测试/自定义路径注入。</summary>
    public JsonSctPolicyStore(string filePath) => _filePath = filePath;

    /// <inheritdoc />
    public SctPolicyOptions Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new SctPolicyOptions();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<SctPolicyOptions>(json) ?? new SctPolicyOptions();
        }
        catch (JsonException)
        {
            // 配置损坏时回退默认值（不阻断启动；留待下次保存覆盖）
            return new SctPolicyOptions();
        }
    }

    /// <inheritdoc />
    public void Save(SctPolicyOptions options)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_filePath, JsonSerializer.Serialize(options, _json));
    }
}
