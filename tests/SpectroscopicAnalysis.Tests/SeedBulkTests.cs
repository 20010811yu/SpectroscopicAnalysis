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

    [Fact]
    public void ControlLimit_仅允许关联控制样()
    {
        var path = Path.Combine(Path.GetTempPath(), $"seedtest_{Guid.NewGuid():N}.db");
        try
        {
            var db = new Db(path);
            new DbInitializer(db).EnsureCreated();
            using var conn = db.Open();

            // 铺底：一个 Control 样 + 一个 SettingUp 样
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO SCT_Sample (SampleID, SampleName, SampleType, InitialThickness, CurrentThickness, MinThickness, UsageCount, MaxUsageCount, Status)
                    VALUES ('CTL-T1', '控制样 T1', 'Control', 10.0, 10.0, 6.0, 0, 60, 'Normal'),
                           ('SUS-T1', '设置样 T1', 'SettingUp', 10.0, 10.0, 8.0, 0, 50, 'Normal')
                    """;
                cmd.ExecuteNonQuery();
            }

            void InsertLimit(string sampleId)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO ControlLimit (SampleID, Element, UpperLimit, LowerLimit, LimitType)
                    VALUES ($id, 'Si', 1.0, 0.0, 'Absolute')
                    """;
                cmd.Parameters.AddWithValue("$id", sampleId);
                cmd.ExecuteNonQuery();
            }

            InsertLimit("CTL-T1");   // 控制样 → 成功

            var ex = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => InsertLimit("SUS-T1"));
            Assert.Contains("控制限仅允许关联控制样", ex.Message);

            // 存量清理：先 DROP 触发器模拟旧库（旧库无触发器），插入违规行后 EnsureCreated 应重建触发器并清除违规行
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DROP TRIGGER trg_ControlLimit_ControlOnly";
                cmd.ExecuteNonQuery();
                cmd.CommandText = """
                    INSERT INTO ControlLimit (LimitID, SampleID, Element, UpperLimit, LowerLimit, LimitType)
                    SELECT 99999, SampleID, 'Si', 1.0, 0.0, 'Absolute' FROM SCT_Sample WHERE SampleID = 'SUS-T1'
                    """;
                cmd.ExecuteNonQuery();
            }
            new DbInitializer(db).EnsureCreated();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM ControlLimit WHERE SampleID = 'SUS-T1'";
                Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
            }
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
    public void CorrectionCoefficient_来源仅设置样与类型标准化样()
    {
        var path = Path.Combine(Path.GetTempPath(), $"seedtest_{Guid.NewGuid():N}.db");
        try
        {
            var db = new Db(path);
            new DbInitializer(db).EnsureCreated();
            var seed = new SeedData(db, new SctSampleRepository(db));
            seed.SeedDemo();
            using var conn = db.Open();

            // 种子合规：全部系数的 SourceSampleID 为 NULL 或指向 SettingUp/TypeStd
            long badCount;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COUNT(*) FROM CorrectionCoefficient c
                    WHERE c.SourceSampleID IS NOT NULL
                      AND c.SourceSampleID IN (SELECT SampleID FROM SCT_Sample WHERE SampleType NOT IN ('SettingUp', 'TypeStd'))
                    """;
                badCount = (long)cmd.ExecuteScalar()!;
            }
            Assert.Equal(0L, badCount);

            // 铺底：SettingUp 样 + TypeStd 样 + Control 样
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO SCT_Sample (SampleID, SampleName, SampleType, InitialThickness, CurrentThickness, MinThickness, UsageCount, MaxUsageCount, Status)
                    VALUES ('SUS-T2', '设置样 T2', 'SettingUp', 10.0, 10.0, 8.0, 0, 50, 'Normal'),
                           ('TS-T2-01', '类型标准化样 T2', 'TypeStd', 10.0, 10.0, 7.0, 0, 40, 'Normal'),
                           ('CTL-T2', '控制样 T2', 'Control', 10.0, 10.0, 6.0, 0, 60, 'Normal')
                    """;
                cmd.ExecuteNonQuery();
            }

            void InsertCoeff(string sourceId)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO CorrectionCoefficient (MethodID, Element, Slope, Intercept, EffectiveTime, SourceSampleID, IsCurrent, Version)
                    VALUES ('M-T', 'Si', 1.0, 0.0, '2026-01-01 00:00:00', $src, 0, 1)
                    """;
                cmd.Parameters.AddWithValue("$src", sourceId);
                cmd.ExecuteNonQuery();
            }

            InsertCoeff("SUS-T2");      // 设置样 → 成功
            InsertCoeff("TS-T2-01");    // 类型标准化样 → 成功

            var ex = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => InsertCoeff("CTL-T2"));
            Assert.Contains("校正系数来源样品仅允许设置样/类型标准化样", ex.Message);

            // 存量清理：DROP 触发器模拟旧库，插入来源为控制样的违规行 → EnsureCreated 后被清除
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DROP TRIGGER trg_CorrectionCoefficient_SourceSampleType";
                cmd.ExecuteNonQuery();
                cmd.CommandText = """
                    INSERT INTO CorrectionCoefficient (CoeffID, MethodID, Element, Slope, Intercept, EffectiveTime, SourceSampleID, IsCurrent, Version)
                    SELECT 99999, MethodID, Element, Slope, Intercept, EffectiveTime, 'CTL-T2', 0, 1
                    FROM CorrectionCoefficient WHERE SourceSampleID = 'SUS-T2' LIMIT 1
                    """;
                cmd.ExecuteNonQuery();
            }
            new DbInitializer(db).EnsureCreated();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM CorrectionCoefficient WHERE SourceSampleID = 'CTL-T2'";
                Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
            }
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
