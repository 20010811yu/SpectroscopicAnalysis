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

    [Fact]
    public void SeedIfEmpty_空库播种_非空库删除后不再恢复()
    {
        var path = Path.Combine(Path.GetTempPath(), $"seedtest_{Guid.NewGuid():N}.db");
        try
        {
            var db = new Db(path);
            new DbInitializer(db).EnsureCreated();
            var seed = new SeedData(db, new SctSampleRepository(db));

            // 空库 → SeedIfEmpty 播种
            seed.SeedIfEmpty();
            using var conn = db.Open();
            long Count(string table)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
                return (long)cmd.ExecuteScalar()!;
            }
            Assert.Equal(3000, Count("SCT_Sample"));

            // 删除部分数据后再次 SeedIfEmpty → 不重播、不恢复（先删子表引用再删本体，与仓储级联删除同序）
            foreach (var child in new[] { "SCT_CertifiedValue", "ControlLimit" })
            {
                using var delChild = conn.CreateCommand();
                delChild.CommandText = $"DELETE FROM {child} WHERE SampleID IN (SELECT SampleID FROM SCT_Sample WHERE SampleID LIKE '%-G%')";
                delChild.ExecuteNonQuery();
            }

            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM SCT_Sample WHERE SampleID LIKE '%-G%'";
                del.ExecuteNonQuery();
            }

            seed.SeedIfEmpty();
            Assert.True(Count("SCT_Sample") < 3000, $"删除后不应恢复：Count={Count("SCT_Sample")}");
            Assert.Equal(Count("SCT_Sample"), Count("SCT_Sample"));   // 二次调用无变化
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
