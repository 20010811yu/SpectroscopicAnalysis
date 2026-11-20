using SpectroscopicAnalysis.Host.Data.Database;
using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>SCT 操作日志仓储实现（查询 + 审计写入 + 删除）：参数化 SQL（开发规范第 5 节）。</summary>
public sealed class SctOperationLogRepository : ISctOperationLogRepository
{
    private const string SelectColumns =
        "LogID, SampleID, OperationType, TriggerReason, RawIntensity, MeasuredConc, CorrectedConc, IsPass, " +
        "ThicknessBefore, ThicknessAfter, CoeffBefore, CoeffAfter, StartTime, OperationTime, ResultStatus";

    private readonly Db _db;

    public SctOperationLogRepository(Db db) => _db = db;

    public IReadOnlyList<SctOperationLog> GetAll()
    {
        var list = new List<SctOperationLog>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM SCT_OperationLog ORDER BY OperationTime DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }
        return list;
    }

    public IReadOnlyList<SctOperationLog> GetRecentBySampleId(string sampleId, int limit)
    {
        var list = new List<SctOperationLog>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM SCT_OperationLog WHERE SampleID = $sampleId ORDER BY OperationTime DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$sampleId", sampleId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }
        return list;
    }

    private static SctOperationLog Map(System.Data.Common.DbDataReader reader) => new()
    {
        LogId = reader.GetInt64(0),
        SampleId = reader.IsDBNull(1) ? null : reader.GetString(1),
        OperationType = reader.GetString(2),
        TriggerReason = reader.IsDBNull(3) ? null : reader.GetString(3),
        RawIntensity = reader.IsDBNull(4) ? null : reader.GetString(4),
        MeasuredConc = reader.IsDBNull(5) ? null : reader.GetString(5),
        CorrectedConc = reader.IsDBNull(6) ? null : reader.GetString(6),
        IsPass = reader.IsDBNull(7) ? null : reader.GetInt64(7) != 0,
        ThicknessBefore = reader.IsDBNull(8) ? null : reader.GetDouble(8),
        ThicknessAfter = reader.IsDBNull(9) ? null : reader.GetDouble(9),
        CoeffBefore = reader.IsDBNull(10) ? null : reader.GetString(10),
        CoeffAfter = reader.IsDBNull(11) ? null : reader.GetString(11),
        StartTime = reader.IsDBNull(12) ? null : reader.GetString(12),
        OperationTime = reader.GetString(13),
        ResultStatus = reader.GetString(14),
    };

    /// <summary>插入操作日志（审计留痕，参数化 SQL）。</summary>
    public void Insert(SctOperationLog log)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO SCT_OperationLog (SampleID, OperationType, TriggerReason, RawIntensity, MeasuredConc, CorrectedConc, IsPass, " +
            "ThicknessBefore, ThicknessAfter, CoeffBefore, CoeffAfter, StartTime, OperationTime, ResultStatus) " +
            "VALUES ($sampleId, $opType, $reason, $raw, $measured, $corrected, $isPass, $thkBefore, $thkAfter, $coeffBefore, $coeffAfter, $startTime, $time, $status)";
        cmd.Parameters.AddWithValue("$sampleId", (object?)log.SampleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$opType", log.OperationType);
        cmd.Parameters.AddWithValue("$reason", (object?)log.TriggerReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$raw", (object?)log.RawIntensity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$measured", (object?)log.MeasuredConc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$corrected", (object?)log.CorrectedConc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$isPass", log.IsPass.HasValue ? (log.IsPass.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$thkBefore", (object?)log.ThicknessBefore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thkAfter", (object?)log.ThicknessAfter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$coeffBefore", (object?)log.CoeffBefore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$coeffAfter", (object?)log.CoeffAfter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$startTime", (object?)log.StartTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$time", log.OperationTime);
        cmd.Parameters.AddWithValue("$status", log.ResultStatus);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long logId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM SCT_OperationLog WHERE LogID = $id";
        cmd.Parameters.AddWithValue("$id", logId);
        cmd.ExecuteNonQuery();
    }
}
