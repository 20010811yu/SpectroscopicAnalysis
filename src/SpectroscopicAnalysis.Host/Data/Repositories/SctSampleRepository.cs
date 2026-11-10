using Microsoft.Data.Sqlite;
using SpectroscopicAnalysis.Host.Data.Database;
using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>SCT 样品档案仓储实现：参数化 SQL + 写操作事务（开发规范第 5 节）。</summary>
public sealed class SctSampleRepository : ISctSampleRepository
{
    private const string SelectColumns =
        "SampleID, SampleName, SampleType, AlloyType, RackPosition, AssociatedMethod, InitialThickness, CurrentThickness, " +
        "MinThickness, UsageCount, MaxUsageCount, Status, LastUseTime, LastHeightCheckTime, IsActive, CreatedTime";

    private readonly Db _db;

    public SctSampleRepository(Db db) => _db = db;

    public SctSample? GetById(string sampleId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM SCT_Sample WHERE SampleID = $id";
        cmd.Parameters.AddWithValue("$id", sampleId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<SctSample> GetAll()
    {
        var list = new List<SctSample>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM SCT_Sample ORDER BY SampleID";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }
        return list;
    }

    public void Insert(SctSample sample)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO SCT_Sample (SampleID, SampleName, SampleType, AlloyType, RackPosition, AssociatedMethod,
                InitialThickness, CurrentThickness, MinThickness, UsageCount, MaxUsageCount,
                Status, LastUseTime, LastHeightCheckTime, IsActive, CreatedTime)
            VALUES ($id, $name, $type, $alloy, $rack, $method, $initThk, $curThk, $minThk, $usage, $maxUsage,
                $status, $lastUse, $lastHeight, $active, COALESCE($created, datetime('now','localtime')))
            """;
        Fill(cmd, sample);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public void Update(SctSample sample)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE SCT_Sample SET
                SampleName = $name, SampleType = $type, AlloyType = $alloy, RackPosition = $rack,
                AssociatedMethod = $method,
                InitialThickness = $initThk, CurrentThickness = $curThk, MinThickness = $minThk,
                UsageCount = $usage, MaxUsageCount = $maxUsage, Status = $status,
                LastUseTime = $lastUse, LastHeightCheckTime = $lastHeight, IsActive = $active
            WHERE SampleID = $id
            """;
        Fill(cmd, sample);
        if (cmd.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException($"样品不存在：{sample.SampleId}");
        }
        tx.Commit();
    }

    public void Delete(string sampleId)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM SCT_Sample WHERE SampleID = $id";
        cmd.Parameters.AddWithValue("$id", sampleId);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>单事务级联删除：控制限 → 标准值 → 样品本体；操作日志保留（审计追溯）。</summary>
    public void DeleteWithChildren(string sampleId)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var table in new[] { "ControlLimit", "SCT_CertifiedValue", "SCT_Sample" })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE SampleID = $id";
            cmd.Parameters.AddWithValue("$id", sampleId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>激发后持久化（事务）：次数 +1、当前厚度、状态、上次使用时间。</summary>
    public void UpdateAfterSpark(string sampleId, double currentThickness, string status, DateTime usedAt)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE SCT_Sample SET
                UsageCount = UsageCount + 1,
                CurrentThickness = $thk,
                Status = $status,
                LastUseTime = $usedAt
            WHERE SampleID = $id
            """;
        cmd.Parameters.AddWithValue("$thk", currentThickness);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$usedAt", FormatTime(usedAt));
        cmd.Parameters.AddWithValue("$id", sampleId);
        if (cmd.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException($"样品不存在：{sampleId}");
        }
        tx.Commit();
    }

    private static void Fill(SqliteCommand cmd, SctSample sample)
    {
        cmd.Parameters.AddWithValue("$id", sample.SampleId);
        cmd.Parameters.AddWithValue("$name", sample.SampleName);
        cmd.Parameters.AddWithValue("$type", sample.SampleType);
        cmd.Parameters.AddWithValue("$alloy", (object?)sample.AlloyType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rack", (object?)sample.RackPosition ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$method", (object?)sample.AssociatedMethod ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$initThk", sample.InitialThickness);
        cmd.Parameters.AddWithValue("$curThk", sample.CurrentThickness);
        cmd.Parameters.AddWithValue("$minThk", sample.MinThickness);
        cmd.Parameters.AddWithValue("$usage", sample.UsageCount);
        cmd.Parameters.AddWithValue("$maxUsage", sample.MaxUsageCount);
        cmd.Parameters.AddWithValue("$status", sample.Status);
        // 可空时间戳必须转 DBNull.Value（C# null 会使 SqliteParameter 绑定抛 "Value must be set"）
        cmd.Parameters.AddWithValue("$lastUse", (object?)FormatTime(sample.LastUseTime) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastHeight", (object?)FormatTime(sample.LastHeightCheckTime) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$active", sample.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", (object?)FormatTime(sample.CreatedTime) ?? DBNull.Value);
    }

    private static SctSample Map(SqliteDataReader r)
    {
        var s = new SctSample
        {
            SampleId = r.GetString(0),
            SampleName = r.GetString(1),
            SampleType = r.GetString(2),
            AlloyType = r.IsDBNull(3) ? null : r.GetString(3),
            RackPosition = r.IsDBNull(4) ? null : r.GetString(4),
            AssociatedMethod = r.IsDBNull(5) ? null : r.GetString(5),
            InitialThickness = r.GetDouble(6),
            CurrentThickness = r.GetDouble(7),
            MinThickness = r.GetDouble(8),
            UsageCount = r.GetInt32(9),
            MaxUsageCount = r.GetInt32(10),
            Status = r.GetString(11),
            LastUseTime = ParseTime(r, 12),
            LastHeightCheckTime = ParseTime(r, 13),
            IsActive = r.GetInt64(14) != 0,
            CreatedTime = ParseTime(r, 15),
        };
        return s;
    }

    private static string? FormatTime(DateTime? time) =>
        time?.ToString("yyyy-MM-dd HH:mm:ss");

    private static DateTime? ParseTime(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : DateTime.Parse(r.GetString(ordinal));
}
