using SpectroscopicAnalysis.Host.Data.Database;
using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>控制限仓储实现：参数化 SQL（开发规范第 5 节）。</summary>
public sealed class ControlLimitRepository : IControlLimitRepository
{
    private const string SelectColumns = "LimitID, SampleID, Element, UpperLimit, LowerLimit, LimitType";

    private readonly Db _db;

    public ControlLimitRepository(Db db) => _db = db;

    public IReadOnlyList<ControlLimit> GetAll()
    {
        var list = new List<ControlLimit>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM ControlLimit ORDER BY SampleID, Element";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ControlLimit
            {
                LimitId = reader.GetInt64(0),
                SampleId = reader.GetString(1),
                Element = reader.GetString(2),
                UpperLimit = reader.GetDouble(3),
                LowerLimit = reader.GetDouble(4),
                LimitType = reader.GetString(5),
            });
        }
        return list;
    }

    public void Delete(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ControlLimit WHERE LimitID = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
