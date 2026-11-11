using Microsoft.Data.Sqlite;
using SpectroscopicAnalysis.Host.Data.Database;
using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>生产样品档案仓储实现：参数化 SQL + keyset 游标分页（开发规范第 5 节）。</summary>
public sealed class ProductionSampleRepository : IProductionSampleRepository
{
    private const string SelectColumns =
        "SampleID, SampleName, SampleType, AlloyType, RackPosition, AssociatedMethod, Status, CreatedTime, Remark";

    private readonly Db _db;

    public ProductionSampleRepository(Db db) => _db = db;

    /// <summary>生产样游标分页：日期区间 + SampleID 游标，每次仅取 pageSize 条。</summary>
    public IReadOnlyList<ProductionSample> GetPage(DateTime? createdOn, string? afterSampleId, string? beforeSampleId, int pageSize)
    {
        var list = new List<ProductionSample>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        // 向后翻：SampleID > 游标升序；向前翻：SampleID < 游标降序取后再反转为升序返回
        var backward = beforeSampleId is not null;
        cmd.CommandText = $"""
            SELECT {SelectColumns} FROM Production_Sample
            WHERE ($day IS NULL OR (CreatedTime >= $day AND CreatedTime < $dayNext))
              AND ($after IS NULL OR SampleID > $after)
              AND ($before IS NULL OR SampleID < $before)
            ORDER BY SampleID {(backward ? "DESC" : "ASC")}
            LIMIT $limit
            """;
        BindDate(cmd, createdOn);
        cmd.Parameters.AddWithValue("$after", (object?)afterSampleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$before", (object?)beforeSampleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", pageSize);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }

        if (backward)
        {
            list.Reverse();
        }

        return list;
    }

    /// <summary>符合条件的生产样总数（分页页数计算）。</summary>
    public int Count(DateTime? createdOn)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM Production_Sample
            WHERE ($day IS NULL OR (CreatedTime >= $day AND CreatedTime < $dayNext))
            """;
        BindDate(cmd, createdOn);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>全量生产样（样品档案页台账缓存用；按 SampleID 升序）。</summary>
    public IReadOnlyList<ProductionSample> GetAll()
    {
        var list = new List<ProductionSample>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM Production_Sample ORDER BY SampleID";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }

        return list;
    }

    /// <summary>全量分析结果（按 OperationTime 倒序）。</summary>
    public IReadOnlyList<AnalysisResult> GetAllResults()
    {
        var list = new List<AnalysisResult>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ResultID, SampleID, RawIntensity, MeasuredConc, CorrectedConc,
                   CoeffVersion, IsPass, ResultStatus, ArchivePosition, OperationTime
            FROM Analysis_Result ORDER BY OperationTime DESC, ResultID DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(MapResult(reader));
        }

        return list;
    }

    public void Insert(ProductionSample sample)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO Production_Sample (SampleID, SampleName, SampleType, AlloyType, RackPosition,
                AssociatedMethod, Status, CreatedTime, Remark)
            VALUES ($id, $name, $type, $alloy, $rack, $method, $status,
                COALESCE($created, datetime('now','localtime')), $remark)
            """;
        Fill(cmd, sample);
        cmd.ExecuteNonQuery();
    }

    public void InsertResult(AnalysisResult result)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Analysis_Result (SampleID, RawIntensity, MeasuredConc, CorrectedConc,
                CoeffVersion, IsPass, ResultStatus, ArchivePosition, OperationTime)
            VALUES ($id, $raw, $measured, $corrected, $coeff, $pass, $status,
                $archive, COALESCE($time, datetime('now','localtime')))
            """;
        cmd.Parameters.AddWithValue("$id", result.SampleId);
        cmd.Parameters.AddWithValue("$raw", (object?)result.RawIntensity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$measured", (object?)result.MeasuredConc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$corrected", (object?)result.CorrectedConc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$coeff", (object?)result.CoeffVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pass", result.IsPass ? 1 : 0);
        cmd.Parameters.AddWithValue("$status", result.ResultStatus);
        cmd.Parameters.AddWithValue("$archive", (object?)result.ArchivePosition ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$time", (object?)FormatTime(result.OperationTime) ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<AnalysisResult> GetResults(string sampleId)
    {
        var list = new List<AnalysisResult>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ResultID, SampleID, RawIntensity, MeasuredConc, CorrectedConc,
                   CoeffVersion, IsPass, ResultStatus, ArchivePosition, OperationTime
            FROM Analysis_Result WHERE SampleID = $id ORDER BY OperationTime DESC
            """;
        cmd.Parameters.AddWithValue("$id", sampleId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(MapResult(reader));
        }

        return list;
    }

    private static AnalysisResult MapResult(SqliteDataReader r) => new()
    {
        ResultId = r.GetInt64(0),
        SampleId = r.GetString(1),
        RawIntensity = r.IsDBNull(2) ? null : r.GetString(2),
        MeasuredConc = r.IsDBNull(3) ? null : r.GetDouble(3),
        CorrectedConc = r.IsDBNull(4) ? null : r.GetDouble(4),
        CoeffVersion = r.IsDBNull(5) ? null : r.GetInt32(5),
        IsPass = r.GetInt64(6) != 0,
        ResultStatus = r.GetString(7),
        ArchivePosition = r.IsDBNull(8) ? null : r.GetString(8),
        OperationTime = ParseTime(r, 9),
    };

    private static void BindDate(SqliteCommand cmd, DateTime? createdOn)
    {
        cmd.Parameters.AddWithValue("$day", (object?)createdOn?.Date.ToString("yyyy-MM-dd HH:mm:ss") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dayNext", (object?)createdOn?.Date.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss") ?? DBNull.Value);
    }

    private static void Fill(SqliteCommand cmd, ProductionSample sample)
    {
        cmd.Parameters.AddWithValue("$id", sample.SampleId);
        cmd.Parameters.AddWithValue("$name", sample.SampleName);
        cmd.Parameters.AddWithValue("$type", sample.SampleType);
        cmd.Parameters.AddWithValue("$alloy", (object?)sample.AlloyType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rack", (object?)sample.RackPosition ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$method", (object?)sample.AssociatedMethod ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", sample.Status);
        cmd.Parameters.AddWithValue("$created", (object?)FormatTime(sample.CreatedTime) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$remark", (object?)sample.Remark ?? DBNull.Value);
    }

    private static ProductionSample Map(SqliteDataReader r) => new()
    {
        SampleId = r.GetString(0),
        SampleName = r.GetString(1),
        SampleType = r.GetString(2),
        AlloyType = r.IsDBNull(3) ? null : r.GetString(3),
        RackPosition = r.IsDBNull(4) ? null : r.GetString(4),
        AssociatedMethod = r.IsDBNull(5) ? null : r.GetString(5),
        Status = r.GetString(6),
        CreatedTime = ParseTime(r, 7),
        Remark = r.IsDBNull(8) ? null : r.GetString(8),
    };

    private static string? FormatTime(DateTime? time) =>
        time?.ToString("yyyy-MM-dd HH:mm:ss");

    private static DateTime? ParseTime(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : DateTime.Parse(r.GetString(ordinal));
}
