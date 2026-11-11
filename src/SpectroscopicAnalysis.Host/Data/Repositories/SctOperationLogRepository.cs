using SpectroscopicAnalysis.Host.Data.Database;
using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>SCT 操作日志仓储实现（只读）：参数化 SQL（开发规范第 5 节）。</summary>
public sealed class SctOperationLogRepository : ISctOperationLogRepository
{
    private const string SelectColumns =
        "LogID, SampleID, OperationType, TriggerReason, RawIntensity, MeasuredConc, CorrectedConc, IsPass, " +
        "ThicknessBefore, ThicknessAfter, CoeffBefore, CoeffAfter, OperationTime, ResultStatus";

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
            list.Add(new SctOperationLog
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
                OperationTime = reader.GetString(12),
                ResultStatus = reader.GetString(13),
            });
        }
        return list;
    }
}
