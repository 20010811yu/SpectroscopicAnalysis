using SpectroscopicAnalysis.Host.Data.Database;
using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>校正系数仓储实现：参数化 SQL（开发规范第 5 节）。</summary>
public sealed class CorrectionCoefficientRepository : ICorrectionCoefficientRepository
{
    private const string SelectColumns =
        "CoeffID, MethodID, Element, Slope, Intercept, EffectiveTime, SourceSampleID, IsCurrent, ReferenceElement, Version";

    private readonly Db _db;

    public CorrectionCoefficientRepository(Db db) => _db = db;

    public IReadOnlyList<CorrectionCoefficient> GetAll()
    {
        var list = new List<CorrectionCoefficient>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        // 系数表排序：按生效时间倒序（同时间按方法/元素/版本稳定排列）
        cmd.CommandText = $"SELECT {SelectColumns} FROM CorrectionCoefficient ORDER BY EffectiveTime DESC, MethodID, Element, Version DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new CorrectionCoefficient
            {
                CoeffId = reader.GetInt64(0),
                MethodId = reader.GetString(1),
                Element = reader.GetString(2),
                Slope = reader.GetDouble(3),
                Intercept = reader.GetDouble(4),
                EffectiveTime = reader.GetString(5),
                SourceSampleId = reader.IsDBNull(6) ? null : reader.GetString(6),
                IsCurrent = reader.GetInt64(7) != 0,
                ReferenceElement = reader.IsDBNull(8) ? null : reader.GetString(8),
                Version = reader.GetInt32(9),
            });
        }
        return list;
    }

    public void Delete(long coeffId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM CorrectionCoefficient WHERE CoeffID = $id";
        cmd.Parameters.AddWithValue("$id", coeffId);
        cmd.ExecuteNonQuery();
    }
}
