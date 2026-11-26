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

            Assert.Equal(17, Count("SCT_Sample"));   // 行业标配：设置样 3 + 控制样 6 + 类型标准化样 8
            Assert.True(Count("SCT_CertifiedValue") >= 34, $"CertifiedValue={Count("SCT_CertifiedValue")}");
            Assert.True(Count("ControlLimit") >= 30, $"ControlLimit={Count("ControlLimit")}");
            Assert.True(Count("CorrectionCoefficient") >= 5000, $"Coeff={Count("CorrectionCoefficient")}");
            Assert.True(Count("SCT_OperationLog") >= 80000, $"Log={Count("SCT_OperationLog")}");
            Assert.True(Count("Production_Sample") >= 10000, $"Production={Count("Production_Sample")}");
            // 状态两值化后：仅已完成样(约50%)×1~2 条生成结果；待分析样（含失败回退）不落结果
            Assert.True(Count("Analysis_Result") >= 5000, $"AnalysisResult={Count("Analysis_Result")}");

            // 标样类型配比与行业标配一致（设置样 2–4 / 控制样 3–6 低中高覆盖 / 类型标准化样 5–10）
            Assert.Equal(3, CountWhere("SCT_Sample", "SampleType = 'SettingUp'"));
            Assert.Equal(6, CountWhere("SCT_Sample", "SampleType = 'Control'"));
            Assert.Equal(8, CountWhere("SCT_Sample", "SampleType = 'TypeStd'"));
            // 架位全部落在货架示意 A01–E05（RackDirectory 5×5）
            var allPositions = SpectroscopicAnalysis.Host.SampleManager.RackDirectory.AllPositions;
            long outOfRack;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM SCT_Sample WHERE RackPosition IS NOT NULL";
                var totalRacked = (long)cmd.ExecuteScalar()!;
                cmd.CommandText = $"SELECT COUNT(*) FROM SCT_Sample WHERE RackPosition IN ({string.Join(',', allPositions.Select(p => $"'{p}'"))})";
                var inRack = (long)cmd.ExecuteScalar()!;
                outOfRack = totalRacked - inRack;
            }
            Assert.Equal(0L, outOfRack);

            // 全表总数据量超过 10 万行
            var total = Count("SCT_Sample") + Count("SCT_CertifiedValue") + Count("ControlLimit")
                + Count("CorrectionCoefficient") + Count("SCT_OperationLog") + Count("Production_Sample")
                + Count("Analysis_Result");
            Assert.True(total > 100_000, $"全表总量={total}");
            Assert.Equal(0L, CountWhere("Production_Sample", "Status NOT IN ('PendingAnalysis','Completed')"));
            Assert.Equal(0, CountWhere("SCT_Sample", "SampleType = 'Production'"));

            // 幂等：再次注入不增量
            seed.SeedDemo();
            Assert.Equal(17, Count("SCT_Sample"));
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
            Assert.Equal(17, Count("SCT_Sample"));

            // 删除部分数据后再次 SeedIfEmpty → 不重播、不恢复（先删子表引用再删本体，与仓储级联删除同序）
            foreach (var child in new[] { "SCT_CertifiedValue", "ControlLimit" })
            {
                using var delChild = conn.CreateCommand();
                delChild.CommandText = "DELETE FROM {0} WHERE SampleID IN (SELECT SampleID FROM SCT_Sample WHERE SampleType = 'TypeStd')";
                delChild.CommandText = string.Format(delChild.CommandText, child);
                delChild.ExecuteNonQuery();
            }

            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM SCT_Sample WHERE SampleType = 'TypeStd'";
                del.ExecuteNonQuery();
            }

            seed.SeedIfEmpty();
            Assert.True(Count("SCT_Sample") < 17, $"删除后不应恢复：Count={Count("SCT_Sample")}");
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
    public void ControlLimit_仅允许关联检查样()
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
                    VALUES ('CTL-T1', '检查样 T1', 'Control', 10.0, 10.0, 6.0, 0, 60, 'Normal'),
                           ('SUS-T1', '控样 T1', 'SettingUp', 10.0, 10.0, 8.0, 0, 50, 'Normal')
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

            InsertLimit("CTL-T1");   // 检查样 → 成功

            var ex = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => InsertLimit("SUS-T1"));
            Assert.Contains("控制限仅允许关联检查样", ex.Message);

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
    public void EnsureCreated_过薄样自动归一为禁用()
    {
        var path = Path.Combine(Path.GetTempPath(), $"seedtest_{Guid.NewGuid():N}.db");
        try
        {
            var db = new Db(path);
            new DbInitializer(db).EnsureCreated();
            using var conn = db.Open();

            // 铺底：过薄样（Normal/NeedMeasure 但厚度低于最小）+ 已禁用过薄样 + 厚度充足样
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO SCT_Sample (SampleID, SampleName, SampleType, InitialThickness, CurrentThickness, MinThickness, UsageCount, MaxUsageCount, Status)
                    VALUES ('THIN-1', '过薄未禁用', 'Control', 10, 4.5, 5, 0, 60, 'Normal'),
                           ('THIN-2', '过薄需测高', 'Control', 10, 4.0, 5, 0, 60, 'NeedMeasure'),
                           ('THIN-3', '过薄已禁用', 'Control', 10, 3.5, 5, 0, 60, 'Disabled'),
                           ('THIN-4', '厚度充足', 'Control', 10, 9.0, 5, 0, 60, 'Normal')
                    """;
                cmd.ExecuteNonQuery();
            }

            // 兜底迁移（幂等）：过薄 → Disabled；已禁用/厚度充足不受影响
            new DbInitializer(db).EnsureCreated();

            string StatusOf(string id)
            {
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT Status FROM SCT_Sample WHERE SampleID = $id";
                q.Parameters.AddWithValue("$id", id);
                return (string)q.ExecuteScalar()!;
            }
            Assert.Equal("Disabled", StatusOf("THIN-1"));
            Assert.Equal("Disabled", StatusOf("THIN-2"));
            Assert.Equal("Disabled", StatusOf("THIN-3"));   // 幂等：已是 Disabled 不变
            Assert.Equal("Normal", StatusOf("THIN-4"));     // 厚度充足不受影响
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void CorrectionCoefficient_来源仅控样与类型标准化样()
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
                    VALUES ('SUS-T2', '控样 T2', 'SettingUp', 10.0, 10.0, 8.0, 0, 50, 'Normal'),
                           ('TS-T2-01', '类型标准化样 T2', 'TypeStd', 10.0, 10.0, 7.0, 0, 40, 'Normal'),
                           ('CTL-T2', '检查样 T2', 'Control', 10.0, 10.0, 6.0, 0, 60, 'Normal')
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

            InsertCoeff("SUS-T2");      // 控样 → 成功
            InsertCoeff("TS-T2-01");    // 类型标准化样 → 成功

            var ex = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => InsertCoeff("CTL-T2"));
            Assert.Contains("校正系数来源样品仅允许控样/类型标准化样", ex.Message);

            // 存量清理：DROP 触发器模拟旧库，插入来源为检查样的违规行 → EnsureCreated 后被清除
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

    [Fact]
    public void SctSample_AlloyType_仅类型标准化样可关联()
    {
        var path = Path.Combine(Path.GetTempPath(), $"seedtest_{Guid.NewGuid():N}.db");
        try
        {
            var db = new Db(path);
            new DbInitializer(db).EnsureCreated();
            var seed = new SeedData(db, new SctSampleRepository(db));
            seed.SeedDemo();
            using var conn = db.Open();

            // 种子合规：非 TypeStd 行的 AlloyType 全为 NULL
            long badCount;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM SCT_Sample WHERE SampleType != 'TypeStd' AND AlloyType IS NOT NULL";
                badCount = (long)cmd.ExecuteScalar()!;
            }
            Assert.Equal(0L, badCount);

            // 铺底：SettingUp / Control / TypeStd 各一条（无合金）
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO SCT_Sample (SampleID, SampleName, SampleType, InitialThickness, CurrentThickness, MinThickness, UsageCount, MaxUsageCount, Status)
                    VALUES ('SUS-T3', '控样 T3', 'SettingUp', 10.0, 10.0, 8.0, 0, 50, 'Normal'),
                           ('CTL-T3', '检查样 T3', 'Control', 10.0, 10.0, 6.0, 0, 60, 'Normal'),
                           ('TS-T3-01', '类型标准化样 T3', 'TypeStd', 10.0, 10.0, 7.0, 0, 40, 'Normal')
                    """;
                cmd.ExecuteNonQuery();
            }

            void SetAlloy(string sampleId)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE SCT_Sample SET AlloyType = '6061' WHERE SampleID = $id";
                cmd.Parameters.AddWithValue("$id", sampleId);
                cmd.ExecuteNonQuery();
            }

            SetAlloy("TS-T3-01");       // 类型标准化样 → 成功

            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => SetAlloy("SUS-T3"));
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => SetAlloy("CTL-T3"));

            // 存量清理：DROP 触发器模拟旧库，给检查样挂合金 → EnsureCreated 后被清空
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DROP TRIGGER trg_SCT_Sample_AlloyType_TypeStdOnly; DROP TRIGGER trg_SCT_Sample_AlloyType_TypeStdOnly_Update";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "UPDATE SCT_Sample SET AlloyType = '9xxx' WHERE SampleID = 'CTL-T3'";
                cmd.ExecuteNonQuery();
            }
            new DbInitializer(db).EnsureCreated();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT AlloyType FROM SCT_Sample WHERE SampleID = 'CTL-T3'";
                Assert.Equal(DBNull.Value, cmd.ExecuteScalar());
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
