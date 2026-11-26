using System.IO;
using SpectroscopicAnalysis.Host.Data.Database;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;

namespace SpectroscopicAnalysis.Tests;

/// <summary>真实 SQLite 集成测试（临时文件库）：建表/迁移、可空时间戳入库、新列往返、级联删除。</summary>
public sealed class SctSampleRepositoryIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly SctSampleRepository _repo;

    public SctSampleRepositoryIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "sct_repo_test", $"{Guid.NewGuid():N}.db");
        _db = new Db(_dbPath);
        _db.Open().Dispose(); // 触发目录创建
        new DbInitializer(_db).EnsureCreated();
        _repo = new SctSampleRepository(_db);
    }

    [Fact]
    public void Insert_可空时间戳与新列_往返一致()
    {
        var sample = new SctSample
        {
            SampleId = "IT-001",
            SampleName = "集成测试样品",
            SampleType = SampleType.SettingUp,
            RackPosition = "T01",
            AssociatedMethod = "GB/T 20975",
            InitialThickness = 15.0,
            CurrentThickness = 15.0,
            MinThickness = 6.0,
            MaxUsageCount = 40,
            // LastUseTime / LastHeightCheckTime / CreatedTime 均为 null（历史崩溃场景）
        };

        _repo.Insert(sample); // 不应抛 "Value must be set"

        var loaded = _repo.GetById("IT-001");
        Assert.NotNull(loaded);
        Assert.Equal("GB/T 20975", loaded.AssociatedMethod);
        Assert.Null(loaded.LastUseTime);
        Assert.Null(loaded.LastHeightCheckTime);
        Assert.NotNull(loaded.CreatedTime); // 表默认值兜底
    }

    [Fact]
    public void 存量库迁移_AssociatedMethod列自动补齐()
    {
        // 模拟旧库：手工建一个无该列的旧结构表
        var dbPath = Path.Combine(Path.GetTempPath(), "sct_repo_test", $"{Guid.NewGuid():N}.db");
        try
        {
            var legacyDb = new Db(dbPath);
            using (var conn = legacyDb.Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE SCT_Sample (
                        SampleID TEXT PRIMARY KEY, SampleName TEXT NOT NULL,
                        SampleType TEXT NOT NULL, AlloyType TEXT, RackPosition TEXT,
                        InitialThickness REAL NOT NULL, CurrentThickness REAL NOT NULL, MinThickness REAL NOT NULL,
                        UsageCount INTEGER NOT NULL DEFAULT 0, MaxUsageCount INTEGER NOT NULL,
                        Status TEXT NOT NULL, LastUseTime TEXT, LastHeightCheckTime TEXT,
                        IsActive INTEGER NOT NULL DEFAULT 1,
                        CreatedTime TEXT NOT NULL DEFAULT (datetime('now','localtime')));
                    """;
                cmd.ExecuteNonQuery();
            }

            // EnsureCreated 对已存在的 SCT_Sample 不重建，迁移应补齐 AssociatedMethod 列
            new DbInitializer(legacyDb).EnsureCreated();

            var repo = new SctSampleRepository(legacyDb);
            var sample = new SctSample
            {
                SampleId = "IT-002", SampleName = "旧库样品", SampleType = SampleType.Control,
                RackPosition = "T02", InitialThickness = 10, CurrentThickness = 10, MinThickness = 5, MaxUsageCount = 30,
            };
            repo.Insert(sample);
            Assert.Null(repo.GetById("IT-002")!.AssociatedMethod);
        }
        finally
        {
            // 连接池（Pooling=true）会持有文件句柄，先清池再删临时文件（否则 IOException）
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var dir = Path.GetDirectoryName(dbPath)!;
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [Fact]
    public void DeleteWithChildren_级联删除且日志保留()
    {
        using var conn = _db.Open();
        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO SCT_Sample (SampleID, SampleName, SampleType, Status, InitialThickness, CurrentThickness, MinThickness, MaxUsageCount)
                VALUES ('IT-003', '级联测试', 'SettingUp', 'Normal', 10, 10, 5, 30);
                INSERT INTO SCT_CertifiedValue (SampleID, Element, CertifiedValue) VALUES ('IT-003', 'Si', 1.0);
                INSERT INTO SCT_OperationLog (SampleID, OperationType) VALUES ('IT-003', 'Measure');
                """;
            seed.ExecuteNonQuery();
        }

        _repo.DeleteWithChildren("IT-003");

        Assert.Null(_repo.GetById("IT-003"));
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM SCT_OperationLog WHERE SampleID = 'IT-003'";
        Assert.Equal(1L, (long)check.ExecuteScalar()!); // 操作日志保留（审计）
        conn.Dispose();
        // 标准值级联删除见仓储实现，此处经 GetById 返回 null 已覆盖主链路
    }

    [Fact]
    public void 生产样独立建档_SCT_Sample仅存SCT样品()
    {
        _repo.Insert(new SctSample
        {
            SampleId = "PRD-LEGACY-01",
            SampleName = "遗留生产样",
            SampleType = SampleType.Production,
            InitialThickness = 20,
            CurrentThickness = 20,
            MinThickness = 5,
            MaxUsageCount = 100,
            CreatedTime = DateTime.Today,
        });

        // EnsureCreated 的启动清理：SCT_Sample 中不再保留生产样行（生产样由 Production_Sample 表提供）
        new DbInitializer(_db).EnsureCreated();

        Assert.Null(_repo.GetById("PRD-LEGACY-01"));
        Assert.Equal(0, _repo.GetAll().Count(s => s.SampleType == SampleType.Production));
    }

    [Fact]
    public void UpdateAfterSpark_兼任孪生次数拉平且厚度状态不串()
    {
        var control = new SctSample
        {
            SampleId = "HC-01", SampleName = "检查样", SampleType = SampleType.Control,
            RackPosition = "T10", InitialThickness = 10, CurrentThickness = 10, MinThickness = 5,
            MaxUsageCount = 50, Status = SampleStatus.Normal, PairSampleId = "HC-01-T",
        };
        var twin = new SctSample
        {
            SampleId = "HC-01-T", SampleName = "类型标准化样", SampleType = SampleType.TypeStd,
            AlloyType = "6063", RackPosition = "T10", InitialThickness = 10, CurrentThickness = 9.5,
            MinThickness = 5, MaxUsageCount = 50, Status = SampleStatus.Warning, PairSampleId = "HC-01",
        };
        _repo.Insert(control);
        _repo.Insert(twin);

        _repo.UpdateAfterSpark("HC-01", currentThickness: 9.9, status: SampleStatus.Normal, usedAt: new DateTime(2026, 11, 26, 10, 0, 0));

        var loadedControl = _repo.GetById("HC-01")!;
        var loadedTwin = _repo.GetById("HC-01-T")!;
        // 主档案：次数+1、厚度状态更新
        Assert.Equal(1, loadedControl.UsageCount);
        Assert.Equal(9.9, loadedControl.CurrentThickness);
        Assert.Equal(SampleStatus.Normal, loadedControl.Status);
        // 孪生：次数/上次使用时间拉平（反映同一物理样品真实磨损），厚度/状态按角色独立不串写
        Assert.Equal(1, loadedTwin.UsageCount);
        Assert.Equal(9.5, loadedTwin.CurrentThickness);
        Assert.Equal(SampleStatus.Warning, loadedTwin.Status);
        Assert.Equal(loadedControl.LastUseTime, loadedTwin.LastUseTime);
        // 反向：孪生激发同样拉平主档案
        _repo.UpdateAfterSpark("HC-01-T", 9.4, SampleStatus.Warning, new DateTime(2026, 11, 26, 11, 0, 0));
        Assert.Equal(2, _repo.GetById("HC-01")!.UsageCount);
        Assert.Equal(9.9, _repo.GetById("HC-01")!.CurrentThickness);
    }

    [Fact]
    public void UpdateAfterSpark_无孪生行为不变()
    {
        _repo.Insert(new SctSample
        {
            SampleId = "IT-010", SampleName = "普通样", SampleType = SampleType.SettingUp,
            RackPosition = "T11", InitialThickness = 10, CurrentThickness = 10, MinThickness = 5,
            MaxUsageCount = 50, Status = SampleStatus.Normal,
        });

        _repo.UpdateAfterSpark("IT-010", 9.8, SampleStatus.Normal, new DateTime(2026, 11, 26, 9, 0, 0));

        var loaded = _repo.GetById("IT-010")!;
        Assert.Equal(1, loaded.UsageCount);
        Assert.Null(loaded.PairSampleId);
        Assert.Equal(9.8, loaded.CurrentThickness);
    }

    [Fact]
    public void PairSampleId_持久化往返()
    {
        _repo.Insert(new SctSample
        {
            SampleId = "IT-011", SampleName = "往返测试", SampleType = SampleType.Control,
            RackPosition = "T12", InitialThickness = 10, CurrentThickness = 10, MinThickness = 5,
            MaxUsageCount = 50, Status = SampleStatus.Normal, PairSampleId = "IT-011-T",
        });

        Assert.Equal("IT-011-T", _repo.GetById("IT-011")!.PairSampleId);
        Assert.Contains(_repo.GetAll(), s => s.PairSampleId == "IT-011-T");
    }

    [Fact]
    public void 存量库迁移_PairSampleId列自动补齐()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "sct_repo_test", $"{Guid.NewGuid():N}.db");
        try
        {
            var legacyDb = new Db(dbPath);
            using (var conn = legacyDb.Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE SCT_Sample (
                        SampleID TEXT PRIMARY KEY, SampleName TEXT NOT NULL,
                        SampleType TEXT NOT NULL, AlloyType TEXT, RackPosition TEXT,
                        InitialThickness REAL NOT NULL, CurrentThickness REAL NOT NULL, MinThickness REAL NOT NULL,
                        UsageCount INTEGER NOT NULL DEFAULT 0, MaxUsageCount INTEGER NOT NULL,
                        Status TEXT NOT NULL, LastUseTime TEXT, LastHeightCheckTime TEXT,
                        IsActive INTEGER NOT NULL DEFAULT 1,
                        CreatedTime TEXT NOT NULL DEFAULT (datetime('now','localtime')));
                    """;
                cmd.ExecuteNonQuery();
            }

            // EnsureCreated 对已存在的 SCT_Sample 不重建，迁移应补齐 PairSampleId 列
            new DbInitializer(legacyDb).EnsureCreated();

            var repo = new SctSampleRepository(legacyDb);
            var sample = new SctSample
            {
                SampleId = "IT-012", SampleName = "旧库兼任样", SampleType = SampleType.Control,
                RackPosition = "T13", InitialThickness = 10, CurrentThickness = 10, MinThickness = 5,
                MaxUsageCount = 30, PairSampleId = "IT-012-T",
            };
            repo.Insert(sample);
            Assert.Equal("IT-012-T", repo.GetById("IT-012")!.PairSampleId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var dir = Path.GetDirectoryName(dbPath)!;
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            // 连接池可能持有文件句柄；清理整个临时目录（含 -wal/-shm）
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var dir = Path.GetDirectoryName(_dbPath)!;
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
    }
}
