using System.Data;
using System.Data.Common;

namespace SpectroscopicAnalysis.Host.Data.Database;

/// <summary>
/// 建表脚本：字段定义见 docs/上位机任务清单.md 第三节（P19–21）与进阶设计 1.3 节。
/// 全部幂等（IF NOT EXISTS），重复执行安全。
/// </summary>
public sealed class DbInitializer
{
    private readonly Db _db;

    public DbInitializer(Db db) => _db = db;

    private static readonly string[] Scripts =
    {
        // 样品档案表
        """
        CREATE TABLE IF NOT EXISTS SCT_Sample (
            SampleID            TEXT PRIMARY KEY,
            SampleName          TEXT NOT NULL,
            SampleType          TEXT NOT NULL CHECK (SampleType IN ('SettingUp','Control','TypeStd','Production')),
            AlloyType           TEXT,
            RackPosition        TEXT,
            AssociatedMethod    TEXT,
            InitialThickness    REAL NOT NULL,
            CurrentThickness    REAL NOT NULL,
            MinThickness        REAL NOT NULL,
            UsageCount          INTEGER NOT NULL DEFAULT 0,
            MaxUsageCount       INTEGER NOT NULL,
            Status              TEXT NOT NULL CHECK (Status IN ('Normal','NeedMeasure','Warning','Disabled')),
            LastUseTime         TEXT,
            LastHeightCheckTime TEXT,
            IsActive            INTEGER NOT NULL DEFAULT 1,
            CreatedTime         TEXT NOT NULL DEFAULT (datetime('now','localtime'))
        );
        """,
        // 标准值表
        """
        CREATE TABLE IF NOT EXISTS SCT_CertifiedValue (
            ID             INTEGER PRIMARY KEY AUTOINCREMENT,
            SampleID       TEXT NOT NULL REFERENCES SCT_Sample(SampleID),
            Element        TEXT NOT NULL,
            CertifiedValue REAL NOT NULL,
            Unit           TEXT NOT NULL DEFAULT '%',
            Uncertainty    REAL
        );
        """,
        // 校正系数表（版本化：IsCurrent + Version，旧版本归档不删除；不含操作者字段——身份内容已移除）
        // SourceSampleID（来源设置样）外键引用 SCT_Sample.SampleID，可空（NULL 不参与约束）；
        // ON DELETE SET NULL：删除被引用设置样时不阻塞级联删除流程，系数版本保留（来源置空）
        """
        CREATE TABLE IF NOT EXISTS CorrectionCoefficient (
            CoeffID          INTEGER PRIMARY KEY AUTOINCREMENT,
            MethodID         TEXT NOT NULL,
            Element          TEXT NOT NULL,
            Slope            REAL NOT NULL,
            Intercept        REAL NOT NULL,
            EffectiveTime    TEXT NOT NULL,
            SourceSampleID   TEXT REFERENCES SCT_Sample(SampleID) ON DELETE SET NULL,
            IsCurrent        INTEGER NOT NULL DEFAULT 0,
            ReferenceElement TEXT,
            Version          INTEGER NOT NULL DEFAULT 1
        );
        """,
        // SCT 操作日志表（审计全字段，见任务清单 2.4）
        """
        CREATE TABLE IF NOT EXISTS SCT_OperationLog (
            LogID            INTEGER PRIMARY KEY AUTOINCREMENT,
            SampleID         TEXT,
            OperationType    TEXT NOT NULL,
            TriggerReason    TEXT,
            RawIntensity     TEXT,
            MeasuredConc     TEXT,
            CorrectedConc    TEXT,
            IsPass           INTEGER,
            ThicknessBefore  REAL,
            ThicknessAfter   REAL,
            CoeffBefore      TEXT,
            CoeffAfter       TEXT,
            OperationTime    TEXT NOT NULL DEFAULT (datetime('now','localtime')),
            ResultStatus     TEXT NOT NULL DEFAULT 'Success' CHECK (ResultStatus IN ('Success','Fail','Warning'))
        );
        """,
        // 控制限表
        """
        CREATE TABLE IF NOT EXISTS ControlLimit (
            LimitID    INTEGER PRIMARY KEY AUTOINCREMENT,
            SampleID   TEXT NOT NULL REFERENCES SCT_Sample(SampleID),
            Element    TEXT NOT NULL,
            UpperLimit REAL NOT NULL,
            LowerLimit REAL NOT NULL,
            LimitType  TEXT NOT NULL DEFAULT 'Sigma' CHECK (LimitType IN ('Absolute','Percent','Sigma'))
        );
        """,
        // 生产样品档案表（取 SCT_Sample 的生产样子集独立建表，生产样不再存于 SCT_Sample）
        """
        CREATE TABLE IF NOT EXISTS Production_Sample (
            SampleID         TEXT PRIMARY KEY,
            SampleName       TEXT NOT NULL,
            SampleType       TEXT NOT NULL DEFAULT 'Production' CHECK (SampleType = 'Production'),
            AlloyType        TEXT,
            RackPosition     TEXT,
            AssociatedMethod TEXT,
            Status           TEXT NOT NULL DEFAULT 'Normal' CHECK (Status IN ('Normal','Analyzing','Completed','Failed')),
            CreatedTime      TEXT NOT NULL DEFAULT (datetime('now','localtime')),
            Remark           TEXT
        );
        """,
        // 分析结果表（生产样分析归档：原始/校正后双存 + 系数版本可追溯）
        """
        CREATE TABLE IF NOT EXISTS Analysis_Result (
            ResultID        INTEGER PRIMARY KEY AUTOINCREMENT,
            SampleID        TEXT NOT NULL REFERENCES Production_Sample(SampleID) ON DELETE CASCADE,
            RawIntensity    TEXT,
            MeasuredConc    REAL,
            CorrectedConc   REAL,
            CoeffVersion    INTEGER,
            IsPass          INTEGER NOT NULL DEFAULT 1,
            ResultStatus    TEXT NOT NULL DEFAULT 'Success' CHECK (ResultStatus IN ('Success','Fail','Warning')),
            ArchivePosition TEXT,
            OperationTime   TEXT NOT NULL DEFAULT (datetime('now','localtime'))
        );
        """,
        // 常用查询索引
        "CREATE INDEX IF NOT EXISTS IX_OperationLog_Sample_Time ON SCT_OperationLog(SampleID, OperationTime);",
        "CREATE INDEX IF NOT EXISTS IX_Coefficient_Method_Current ON CorrectionCoefficient(MethodID, Element, IsCurrent);",
        "CREATE INDEX IF NOT EXISTS IX_ProductionSample_Status_Time ON Production_Sample(Status, CreatedTime);",
        "CREATE INDEX IF NOT EXISTS IX_AnalysisResult_Sample_Time ON Analysis_Result(SampleID, OperationTime);",
        // 控制限仅允许关联控制样（任务清单第三节硬性约束；SQLite 外键无法跨列限定父表类型，以触发器强制）
        """
        CREATE TRIGGER IF NOT EXISTS trg_ControlLimit_ControlOnly
        BEFORE INSERT ON ControlLimit
        FOR EACH ROW
        WHEN (SELECT SampleType FROM SCT_Sample WHERE SampleID = NEW.SampleID) != 'Control'
        BEGIN
            SELECT RAISE(ABORT, '控制限仅允许关联控制样（SampleType=Control）');
        END;
        """,
        // 校正系数仅来源于设置样/类型标准化样（SourceSampleID 可空；同规则以触发器强制）
        """
        CREATE TRIGGER IF NOT EXISTS trg_CorrectionCoefficient_SourceSampleType
        BEFORE INSERT ON CorrectionCoefficient
        FOR EACH ROW
        WHEN NEW.SourceSampleID IS NOT NULL
             AND (SELECT SampleType FROM SCT_Sample WHERE SampleID = NEW.SourceSampleID) NOT IN ('SettingUp', 'TypeStd')
        BEGIN
            SELECT RAISE(ABORT, '校正系数来源样品仅允许设置样/类型标准化样（SettingUp/TypeStd）');
        END;
        """,
        // SCT 样品合金仅类型标准化样可关联（设置样/控制样不关联合金；生产样 Production_Sample 不受限）
        """
        CREATE TRIGGER IF NOT EXISTS trg_SCT_Sample_AlloyType_TypeStdOnly
        BEFORE INSERT ON SCT_Sample
        FOR EACH ROW
        WHEN NEW.AlloyType IS NOT NULL AND NEW.SampleType != 'TypeStd'
        BEGIN
            SELECT RAISE(ABORT, '合金仅允许关联类型标准化样（SampleType=TypeStd）');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_SCT_Sample_AlloyType_TypeStdOnly_Update
        BEFORE UPDATE OF AlloyType ON SCT_Sample
        FOR EACH ROW
        WHEN NEW.AlloyType IS NOT NULL AND NEW.SampleType != 'TypeStd'
        BEGIN
            SELECT RAISE(ABORT, '合金仅允许关联类型标准化样（SampleType=TypeStd）');
        END;
        """,
    };

    /// <summary>创建全部表与索引（单事务执行）；并迁移存量库缺失列。</summary>
    public void EnsureCreated()
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var sql in Scripts)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        // 存量库迁移：建表脚本对旧库不生效（IF NOT EXISTS），缺失列按表逐个补齐
        AddColumnIfMissing(conn, tx, "SCT_Sample", "AssociatedMethod", "TEXT");
        // 控制限仅允许关联控制样：清理存量库中挂在非 Control 样上的控制限（幂等）
        using (var clean = conn.CreateCommand())
        {
            clean.Transaction = tx;
            clean.CommandText = """
                DELETE FROM ControlLimit
                WHERE SampleID IN (SELECT SampleID FROM SCT_Sample WHERE SampleType != 'Control')
                """;
            clean.ExecuteNonQuery();
        }
        // 校正系数仅来源于设置样/类型标准化样：清理存量库中来源为其他类型的系数行（幂等）
        using (var cleanCoeff = conn.CreateCommand())
        {
            cleanCoeff.Transaction = tx;
            cleanCoeff.CommandText = """
                DELETE FROM CorrectionCoefficient
                WHERE SourceSampleID IS NOT NULL
                  AND SourceSampleID IN (SELECT SampleID FROM SCT_Sample WHERE SampleType NOT IN ('SettingUp', 'TypeStd'))
                """;
            cleanCoeff.ExecuteNonQuery();
        }
        // SCT 样品合金仅类型标准化样：清理存量库中设置样/控制样上的合金值（幂等）
        using (var cleanAlloy = conn.CreateCommand())
        {
            cleanAlloy.Transaction = tx;
            cleanAlloy.CommandText = "UPDATE SCT_Sample SET AlloyType = NULL WHERE SampleType != 'TypeStd' AND AlloyType IS NOT NULL";
            cleanAlloy.ExecuteNonQuery();
        }
        // 生产样档案独立建表后，清理存量库 SCT_Sample 中的生产样行（先删子表引用再删本体，生产样数据由 Production_Sample 提供）
        PurgeProductionFromSctSample(conn, tx);
        tx.Commit();
    }

    /// <summary>清理存量库 SCT_Sample 中的生产样行（幂等）：先删子表引用再删本体；生产样数据由 Production_Sample 表提供。
    /// SCT_OperationLog 为审计日志按规范保留（其 SampleID 无外键约束，不受影响）。</summary>
    private static void PurgeProductionFromSctSample(DbConnection conn, DbTransaction tx)
    {
        foreach (var table in new[] { "ControlLimit", "SCT_CertifiedValue" })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                DELETE FROM {table} WHERE SampleID IN (
                    SELECT SampleID FROM SCT_Sample WHERE SampleType = 'Production'
                )
                """;
            cmd.ExecuteNonQuery();
        }

        using var purge = conn.CreateCommand();
        purge.Transaction = tx;
        purge.CommandText = "DELETE FROM SCT_Sample WHERE SampleType = 'Production'";
        purge.ExecuteNonQuery();
    }

    /// <summary>列不存在时 ALTER TABLE 补列（幂等；SQLite 仅支持 ADD COLUMN）。</summary>
    private static void AddColumnIfMissing(DbConnection conn, DbTransaction tx, string table, string column, string type)
    {
        var exists = false;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            using var alter = conn.CreateCommand();
            alter.Transaction = tx;
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
            alter.ExecuteNonQuery();
        }
    }
}
