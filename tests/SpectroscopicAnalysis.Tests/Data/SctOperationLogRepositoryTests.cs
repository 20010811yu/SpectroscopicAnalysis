using SpectroscopicAnalysis.Host.Data.Database;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Tests.Data;

namespace SpectroscopicAnalysis.Tests.Data;

/// <summary>
/// 操作日志仓储测试：StartTime（开始时间）字段 Insert/GetAll 往返 + 历史行 NULL 兼容。
/// </summary>
public sealed class SctOperationLogRepositoryTests
{
    /// <summary>测试上下文：独立临时库 + 已建表 + 仓储；释放时清理库文件。</summary>
    private sealed class TestContext : IDisposable
    {
        public TestDb Db { get; }
        public SctOperationLogRepository Repo { get; }

        public TestContext()
        {
            Db = new TestDb();
            new DbInitializer(Db.Db).EnsureCreated();
            Repo = new SctOperationLogRepository(Db.Db);
        }

        public void Dispose() => Db.Dispose();
    }

    private static SctOperationLog MakeLog(long id, string? startTime) => new()
    {
        SampleId = "SUS-001",
        OperationType = "Standardization",
        TriggerReason = "任务完成",
        StartTime = startTime,
        OperationTime = "2026-11-20 09:10:32",
        ResultStatus = "Success",
    };

    [Fact]
    public void Insert_GetAll_开始时间往返一致()
    {
        using var ctx = new TestContext();
        ctx.Repo.Insert(MakeLog(1, "2026-11-20 09:10:30"));

        var row = ctx.Repo.GetAll().Single();
        Assert.Equal("2026-11-20 09:10:30", row.StartTime);
        Assert.Equal("2026-11-20 09:10:32", row.OperationTime);
    }

    [Fact]
    public void GetAll_历史行开始时间为空_返回Null()
    {
        using var ctx = new TestContext();
        ctx.Repo.Insert(MakeLog(2, startTime: null));   // 迁移补列后的存量行 StartTime 为 NULL

        var row = ctx.Repo.GetAll().Single();
        Assert.Null(row.StartTime);
    }
}
