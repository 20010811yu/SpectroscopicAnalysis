using Microsoft.Data.Sqlite;
using SpectroscopicAnalysis.Host.Data.Database;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;

namespace SpectroscopicAnalysis.Host.Data;

/// <summary>演示种子数据：与界面原型（docs/prototypes）中展示的样品一致；单事务、幂等，仅空库注入。</summary>
public sealed class SeedData
{
    private readonly Db _db;
    private readonly ISctSampleRepository _samples;

    public SeedData(Db db, ISctSampleRepository samples)
    {
        _db = db;
        _samples = samples;
    }

    /// <summary>任一表数据量不足目标时自动补齐演示数据（INSERT OR IGNORE 幂等；开发阶段便捷，生产环境由人工录入替代）。</summary>
    public void SeedIfEmpty()
    {
        using var conn = _db.Open();
        foreach (var (table, target) in new[] { ("SCT_Sample", SeedTargetCount), ("SCT_CertifiedValue", 5000), ("ControlLimit", 5000), ("CorrectionCoefficient", 5000), ("SCT_OperationLog", 5000) })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            if ((long)cmd.ExecuteScalar()! < target)
            {
                SeedDemo();
                return;
            }
        }
    }

    /// <summary>种子数据目标总量（演示用途：手工演示样 + 生成样凑满 3000 条）。</summary>
    private const int SeedTargetCount = 3000;

    /// <summary>注入演示样品与各子表数据（INSERT OR IGNORE 幂等，单事务）。</summary>
    public void SeedDemo()
    {
        var samples = new List<SctSample>
        {
            new() { SampleId = "SUS-001", SampleName = "设置样 1", SampleType = SampleType.SettingUp, RackPosition = "A01", InitialThickness = 12.0, CurrentThickness = 9.8, MinThickness = 8.0, UsageCount = 50, MaxUsageCount = 50, Status = SampleStatus.NeedMeasure, LastUseTime = DateTime.Parse("2026-11-09 09:40"), LastHeightCheckTime = DateTime.Parse("2026-11-07 08:00") },
            new() { SampleId = "SUS-002", SampleName = "设置样 2", SampleType = SampleType.SettingUp, RackPosition = "A02", InitialThickness = 11.5, CurrentThickness = 11.4, MinThickness = 8.0, UsageCount = 18, MaxUsageCount = 50, Status = SampleStatus.Normal, LastUseTime = DateTime.Parse("2026-11-09 08:02"), LastHeightCheckTime = DateTime.Parse("2026-11-08 08:00") },
            new() { SampleId = "SUS-003", SampleName = "设置样 3", SampleType = SampleType.SettingUp, RackPosition = "A03", InitialThickness = 10.0, CurrentThickness = 7.2, MinThickness = 8.0, UsageCount = 33, MaxUsageCount = 50, Status = SampleStatus.Disabled, LastUseTime = DateTime.Parse("2026-11-05 10:12"), LastHeightCheckTime = DateTime.Parse("2026-11-05 14:20") },
            new() { SampleId = "CTL-001", SampleName = "控制样（低）", SampleType = SampleType.Control, AlloyType = "1xxx", RackPosition = "B01", InitialThickness = 17.0, CurrentThickness = 16.9, MinThickness = 6.0, UsageCount = 22, MaxUsageCount = 60, Status = SampleStatus.Normal, LastHeightCheckTime = DateTime.Parse("2026-11-06 08:00") },
            new() { SampleId = "CTL-002", SampleName = "控制样（中）", SampleType = SampleType.Control, AlloyType = "6xxx", RackPosition = "B02", InitialThickness = 19.0, CurrentThickness = 18.6, MinThickness = 6.0, UsageCount = 41, MaxUsageCount = 60, Status = SampleStatus.Normal, LastHeightCheckTime = DateTime.Parse("2026-11-08 08:00") },
            new() { SampleId = "CTL-003", SampleName = "控制样（高）", SampleType = SampleType.Control, AlloyType = "5xxx", RackPosition = "B03", InitialThickness = 18.0, CurrentThickness = 17.8, MinThickness = 6.0, UsageCount = 9, MaxUsageCount = 60, Status = SampleStatus.Normal, LastHeightCheckTime = DateTime.Parse("2026-11-08 08:00") },
            new() { SampleId = "CTL-004", SampleName = "控制样（低）", SampleType = SampleType.Control, AlloyType = "3xxx", RackPosition = "B04", InitialThickness = 17.5, CurrentThickness = 17.5, MinThickness = 6.0, UsageCount = 0, MaxUsageCount = 60, Status = SampleStatus.Normal },
            new() { SampleId = "TS-6061-01", SampleName = "类型标准化样 6061", SampleType = SampleType.TypeStd, AlloyType = "6061", RackPosition = "C01", InitialThickness = 16.0, CurrentThickness = 15.2, MinThickness = 7.0, UsageCount = 12, MaxUsageCount = 40, Status = SampleStatus.Normal, LastHeightCheckTime = DateTime.Parse("2026-11-01 09:30") },
            new() { SampleId = "TS-1050-01", SampleName = "类型标准化样 1050", SampleType = SampleType.TypeStd, AlloyType = "1050", RackPosition = "C02", InitialThickness = 15.0, CurrentThickness = 14.7, MinThickness = 7.0, UsageCount = 28, MaxUsageCount = 40, Status = SampleStatus.Normal, LastHeightCheckTime = DateTime.Parse("2026-10-30 16:00") },
        };

        samples.AddRange(GenerateSamples(samples.Count));

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        foreach (var s in samples)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO SCT_Sample (SampleID, SampleName, SampleType, AlloyType, RackPosition,
                    InitialThickness, CurrentThickness, MinThickness, UsageCount, MaxUsageCount,
                    Status, LastUseTime, LastHeightCheckTime, IsActive)
                VALUES ($id, $name, $type, $alloy, $rack, $initThk, $curThk, $minThk, $usage, $maxUsage,
                    $status, $lastUse, $lastHeight, 1)
                """;
            cmd.Parameters.AddWithValue("$id", s.SampleId);
            cmd.Parameters.AddWithValue("$name", s.SampleName);
            cmd.Parameters.AddWithValue("$type", s.SampleType);
            cmd.Parameters.AddWithValue("$alloy", (object?)s.AlloyType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rack", (object?)s.RackPosition ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$initThk", s.InitialThickness);
            cmd.Parameters.AddWithValue("$curThk", s.CurrentThickness);
            cmd.Parameters.AddWithValue("$minThk", s.MinThickness);
            cmd.Parameters.AddWithValue("$usage", s.UsageCount);
            cmd.Parameters.AddWithValue("$maxUsage", s.MaxUsageCount);
            cmd.Parameters.AddWithValue("$status", s.Status);
            cmd.Parameters.AddWithValue("$lastUse", (object?)s.LastUseTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lastHeight", (object?)s.LastHeightCheckTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        // SUS-002 标准值（演示）
        InsertCertifiedValue(conn, tx, "SUS-002", "Si", 0.0812, 0.0008);
        InsertCertifiedValue(conn, tx, "SUS-002", "Fe", 0.2045, 0.0015);
        InsertCertifiedValue(conn, tx, "SUS-002", "Cu", 0.0018, 0.0002);

        // CTL-002 控制限（±2σ 示例）
        InsertControlLimit(conn, tx, "CTL-002", "Si", 0.90, 0.70, "Sigma");
        InsertControlLimit(conn, tx, "CTL-002", "Fe", 0.28, 0.14, "Sigma");
        InsertControlLimit(conn, tx, "CTL-002", "Mg", 1.05, 0.85, "Sigma");

        // 批量子表数据：标准值/控制限/校正系数/操作日志各 ≥5000 行（确定性生成）
        SeedBulkSubTables(conn, tx, samples);

        tx.Commit();
    }

    /// <summary>
    /// 批量生成 4 张子表演示数据（确定性：按索引取模，每次种子一致）：
    /// 标准值 = 每样品 2 个元素 × 3000 样品 = 6000 行；
    /// 控制限 = 控制样（约 1000 条）× 5 元素 = 5000 行；
    /// 校正系数 = 4 方法 × 10 元素 × 125 版本 = 5000 行；
    /// 操作日志 = 5000 行（轮转类型/结果状态，时间递减）。
    /// </summary>
    private static void SeedBulkSubTables(SqliteConnection conn, SqliteTransaction tx, List<SctSample> samples)
    {
        var elements = new[] { "Si", "Fe", "Cu", "Mg", "Mn", "Zn", "Cr", "Ni", "Ti", "V" };
        var methods = new[] { "GB/T 7999-A", "GB/T 7999-B", "ASTM E1251-A", "ASTM E1251-B" };
        var opTypes = new[] { "Standardization", "ControlCheck", "HeightMeasure" };
        var controlSamples = samples.Where(s => s.SampleType == SampleType.Control).ToList();
        var baseTime = DateTime.Parse("2026-11-10 08:00");

        // 标准值：每个样品 2 个元素（轮转取元素表），6000 行
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            InsertCertifiedValue(conn, tx, s.SampleId, elements[i % elements.Length], 0.05 + (i % 90) / 100.0, 0.001 + (i % 5) / 10000.0);
            InsertCertifiedValue(conn, tx, s.SampleId, elements[(i + 3) % elements.Length], 0.10 + (i % 50) / 100.0, 0.002 + (i % 3) / 10000.0);
        }

        // 控制限：控制样 × 5 个元素（轮转），≥5000 行
        for (var i = 0; i < controlSamples.Count; i++)
        {
            var s = controlSamples[i];
            for (var j = 0; j < 5; j++)
            {
                var element = elements[(i + j) % elements.Length];
                var upper = 0.60 + ((i + j) % 20) / 20.0;
                var limitType = ((i + j) % 3) switch { 0 => "Sigma", 1 => "Percent", _ => "Absolute" };
                InsertControlLimit(conn, tx, s.SampleId, element, upper, upper - 0.20, limitType);
            }
        }

        // 校正系数：4 方法 × 10 元素 × 125 版本 = 5000 行（IsCurrent 仅最新版本为 1）
        foreach (var method in methods)
        {
            foreach (var element in elements)
            {
                for (var v = 1; v <= 125; v++)
                {
                    InsertCoefficient(conn, tx, method, element, 1.0 + (v % 50) / 100.0, -0.5 + (v % 30) / 100.0,
                        baseTime.AddDays(-125 + v).ToString("yyyy-MM-dd HH:mm:ss"),
                        v % 25 == 0 ? samples[v % samples.Count].SampleId : null,
                        v == 125, v);
                }
            }
        }

        // 操作日志：5000 行（类型/结果轮转，时间按小时递减，关联样品轮转）
        for (var i = 0; i < 5000; i++)
        {
            var opType = opTypes[i % opTypes.Length];
            var status = (i % 10) switch { 8 => "Warning", 9 => "Fail", _ => "Success" };
            var reason = opType switch
            {
                "Standardization" => i % 4 == 0 ? "类型标准化超期" : "累计激发次数到达",
                "ControlCheck" => i % 3 == 0 ? "定时插入" : "人工触发",
                _ => "测高提醒触发",
            };
            var sampleId = samples[i % samples.Count].SampleId;
            InsertOperationLog(conn, tx, i + 1, sampleId, opType, reason,
                baseTime.AddHours(-i).ToString("yyyy-MM-dd HH:mm:ss"), status);
        }
    }

    private static void InsertCertifiedValue(SqliteConnection conn, SqliteTransaction tx, string sampleId, string element, double value, double uncertainty)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO SCT_CertifiedValue (SampleID, Element, CertifiedValue, Unit, Uncertainty)
            VALUES ($sid, $element, $value, '%', $unc)
            """;
        cmd.Parameters.AddWithValue("$sid", sampleId);
        cmd.Parameters.AddWithValue("$element", element);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$unc", uncertainty);
        cmd.ExecuteNonQuery();
    }

    private static void InsertControlLimit(SqliteConnection conn, SqliteTransaction tx, string sampleId, string element, double upper, double lower, string limitType)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO ControlLimit (SampleID, Element, UpperLimit, LowerLimit, LimitType)
            VALUES ($sid, $element, $upper, $lower, $type)
            """;
        cmd.Parameters.AddWithValue("$sid", sampleId);
        cmd.Parameters.AddWithValue("$element", element);
        cmd.Parameters.AddWithValue("$upper", upper);
        cmd.Parameters.AddWithValue("$lower", lower);
        cmd.Parameters.AddWithValue("$type", limitType);
        cmd.ExecuteNonQuery();
    }

    /// <summary>插入校正系数（版本化；不含操作者字段——身份内容已移除）。</summary>
    private static void InsertCoefficient(SqliteConnection conn, SqliteTransaction tx, string methodId, string element,
        double slope, double intercept, string effectiveTime, string? sourceSampleId, bool isCurrent, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO CorrectionCoefficient (MethodID, Element, Slope, Intercept, EffectiveTime,
                SourceSampleID, IsCurrent, Version)
            VALUES ($method, $element, $slope, $intercept, $time, $source, $current, $version)
            """;
        cmd.Parameters.AddWithValue("$method", methodId);
        cmd.Parameters.AddWithValue("$element", element);
        cmd.Parameters.AddWithValue("$slope", slope);
        cmd.Parameters.AddWithValue("$intercept", intercept);
        cmd.Parameters.AddWithValue("$time", effectiveTime);
        cmd.Parameters.AddWithValue("$source", (object?)sourceSampleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$current", isCurrent ? 1 : 0);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.ExecuteNonQuery();
    }

    /// <summary>插入操作日志（审计留痕，全字段演示值）。</summary>
    private static void InsertOperationLog(SqliteConnection conn, SqliteTransaction tx, long logId, string sampleId,
        string opType, string triggerReason, string operationTime, string resultStatus)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO SCT_OperationLog (LogID, SampleID, OperationType, TriggerReason,
                RawIntensity, MeasuredConc, CorrectedConc, IsPass, ThicknessBefore, ThicknessAfter,
                CoeffBefore, CoeffAfter, OperationTime, ResultStatus)
            VALUES ($logId, $sid, $opType, $reason, $raw, $measured, $corrected, $isPass,
                $thkBefore, $thkAfter, $coeffBefore, $coeffAfter, $time, $status)
            """;
        cmd.Parameters.AddWithValue("$logId", logId);
        cmd.Parameters.AddWithValue("$sid", sampleId);
        cmd.Parameters.AddWithValue("$opType", opType);
        cmd.Parameters.AddWithValue("$reason", triggerReason);
        cmd.Parameters.AddWithValue("$raw", $"{{\"I\":{1000 + logId % 500}.5,\"Bg\":{80 + logId % 20}.2}}");
        cmd.Parameters.AddWithValue("$measured", $"{{\"Si\":{0.1 + logId % 50 / 100.0:0.00}}}");
        cmd.Parameters.AddWithValue("$corrected", $"{{\"Si\":{0.1 + logId % 45 / 100.0:0.00}}}");
        cmd.Parameters.AddWithValue("$isPass", resultStatus == "Success" ? 1 : 0);
        cmd.Parameters.AddWithValue("$thkBefore", 10.0 + logId % 100 / 10.0);
        cmd.Parameters.AddWithValue("$thkAfter", 9.9 + logId % 90 / 10.0);
        cmd.Parameters.AddWithValue("$coeffBefore", "{\"a\":1.05,\"b\":-0.20}");
        cmd.Parameters.AddWithValue("$coeffAfter", "{\"a\":1.06,\"b\":-0.21}");
        cmd.Parameters.AddWithValue("$time", operationTime);
        cmd.Parameters.AddWithValue("$status", resultStatus);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 生成补充演示样品（确定性：按索引取模生成，保证每次种子一致）。
    /// 三类轮转覆盖 正常/需测高/禁用 各状态，编号带 G 前缀与手工演示样区分。
    /// </summary>
    private static IEnumerable<SctSample> GenerateSamples(int existingCount)
    {
        var alloys = new[] { "1xxx", "3xxx", "5xxx", "6xxx" };
        var tsAlloys = new[] { "6061", "1050", "3003", "5052" };
        var baseTime = DateTime.Parse("2026-11-09 08:00");

        for (var i = existingCount; i < SeedTargetCount; i++)
        {
            // 三类轮转：0=设置样，1=控制样，2=类型标准化样
            var kind = i % 3;
            var seq = i / 3 + 1;
            // 状态轮转：5 个一组 [正常, 正常, 需测高, 正常, 禁用]
            var status = (seq % 5) switch
            {
                2 => SampleStatus.NeedMeasure,
                4 => SampleStatus.Disabled,
                _ => SampleStatus.Normal,
            };
            var maxUsage = kind == 1 ? 60 : kind == 2 ? 40 : 50;
            var usage = status == SampleStatus.Disabled ? maxUsage : seq * 7 % (maxUsage + 1);
            var initThk = 10.0 + seq % 10 + 0.5; // 10.5 ~ 19.5
            var minThk = kind == 0 ? 8.0 : kind == 1 ? 6.0 : 7.0;
            // 当前厚度：禁用（过薄）压到最小值以下，需测高接近最小值，正常小幅磨损
            var curThk = status == SampleStatus.Disabled
                ? minThk - 0.5
                : status == SampleStatus.NeedMeasure
                    ? minThk + 0.5
                    : initThk - (seq % 4) * 0.3;
            var lastHeight = seq % 3 == 0 ? (DateTime?)baseTime.AddDays(-seq % 15) : null;
            var lastUse = status == SampleStatus.Disabled ? null : seq % 2 == 0 ? (DateTime?)baseTime.AddHours(-seq) : null;

            SctSample s = kind switch
            {
                1 => new() { SampleId = $"CTL-G{seq:000}", SampleName = $"控制样 G-{seq:000}", SampleType = SampleType.Control, AlloyType = alloys[seq % alloys.Length], RackPosition = $"B{seq % 60 + 10:00}", InitialThickness = initThk, CurrentThickness = curThk, MinThickness = minThk, UsageCount = usage, MaxUsageCount = maxUsage, Status = status, LastUseTime = lastUse, LastHeightCheckTime = lastHeight },
                2 => new() { SampleId = $"TS-{tsAlloys[seq % tsAlloys.Length]}-G{seq:00}", SampleName = $"类型标准化样 G-{seq:000}", SampleType = SampleType.TypeStd, AlloyType = tsAlloys[seq % tsAlloys.Length], RackPosition = $"C{seq % 60 + 10:00}", InitialThickness = initThk, CurrentThickness = curThk, MinThickness = minThk, UsageCount = usage, MaxUsageCount = maxUsage, Status = status, LastUseTime = lastUse, LastHeightCheckTime = lastHeight },
                _ => new() { SampleId = $"SUS-G{seq:000}", SampleName = $"设置样 G-{seq:000}", SampleType = SampleType.SettingUp, RackPosition = $"A{seq % 60 + 10:00}", InitialThickness = initThk, CurrentThickness = curThk, MinThickness = minThk, UsageCount = usage, MaxUsageCount = maxUsage, Status = status, LastUseTime = lastUse, LastHeightCheckTime = lastHeight },
            };
            yield return s;
        }
    }
}
