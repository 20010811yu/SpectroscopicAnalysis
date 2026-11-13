using SpectroscopicAnalysis.Host.Data.Database;
using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.Data.Repositories;

/// <summary>标准值仓储实现：参数化 SQL（开发规范第 5 节）。</summary>
public sealed class SctCertifiedValueRepository : ISctCertifiedValueRepository
{
    private const string SelectColumns = "ID, SampleID, Element, CertifiedValue, Unit, Uncertainty";

    private readonly Db _db;

    public SctCertifiedValueRepository(Db db) => _db = db;

    public IReadOnlyList<SctCertifiedValue> GetAll()
    {
        var list = new List<SctCertifiedValue>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM SCT_CertifiedValue ORDER BY SampleID, Element";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }
        return list;
    }

    public IReadOnlyList<SctCertifiedValue> GetBySampleId(string sampleId)
    {
        var list = new List<SctCertifiedValue>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM SCT_CertifiedValue WHERE SampleID = $sampleId ORDER BY Element";
        cmd.Parameters.AddWithValue("$sampleId", sampleId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }
        return list;
    }

    private static SctCertifiedValue Map(System.Data.Common.DbDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        SampleId = reader.GetString(1),
        Element = reader.GetString(2),
        CertifiedValue = reader.GetDouble(3),
        Unit = reader.GetString(4),
        Uncertainty = reader.IsDBNull(5) ? null : reader.GetDouble(5),
    };

    public void Delete(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM SCT_CertifiedValue WHERE ID = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
