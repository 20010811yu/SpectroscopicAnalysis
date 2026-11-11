using System.IO;
using Microsoft.Data.Sqlite;
using SpectroscopicAnalysis.Host.Data;
using SpectroscopicAnalysis.Host.Data.Database;
using SpectroscopicAnalysis.Host.Data.Repositories;
using Xunit;

namespace SpectroscopicAnalysis.Tests;

/// <summary>种子数据集成测试：批量注入行数达标与幂等（真实 SQLite 临时库）。</summary>
public sealed class SeedBulkTests
{
    [Fact]
    public void Seed_批量行数达标()
    {
        var path = Path.Combine(Path.GetTempPath(), $"seedtest_{Guid.NewGuid():N}.db");
        try
        {
            var db = new Db(path);
            new DbInitializer(db).EnsureCreated();
            var seed = new SeedData(db, new SctSampleRepository(db));
            seed.SeedDemo();

            using var conn = db.Open();
            long Count(string table)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
                return (long)cmd.ExecuteScalar()!;
            }
            long CountWhere(string table, string where)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where}";
                return (long)cmd.ExecuteScalar()!;
            }

            Assert.Equal(3000, Count("SCT_Sample"));
            Assert.True(Count("SCT_CertifiedValue") >= 5000, $"CertifiedValue={Count("SCT_CertifiedValue")}");
            Assert.True(Count("ControlLimit") >= 5000, $"ControlLimit={Count("ControlLimit")}");
            Assert.True(Count("CorrectionCoefficient") >= 5000, $"Coeff={Count("CorrectionCoefficient")}");
            Assert.True(Count("SCT_OperationLog") >= 5000, $"Log={Count("SCT_OperationLog")}");
            Assert.True(Count("Production_Sample") >= 3000, $"Production={Count("Production_Sample")}");
            Assert.True(Count("Analysis_Result") >= 3000, $"AnalysisResult={Count("Analysis_Result")}");
            Assert.Equal(0, CountWhere("SCT_Sample", "SampleType = 'Production'"));

            // 幂等：再次注入不增量
            seed.SeedDemo();
            Assert.Equal(3000, Count("SCT_Sample"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
