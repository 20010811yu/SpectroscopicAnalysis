using System.IO;
using SpectroscopicAnalysis.Host.Data.Database;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;

namespace SpectroscopicAnalysis.Tests;

/// <summary>生产样仓储真实 SQLite 集成测试（临时文件库）：keyset 游标分页、日期过滤、结果插入与查询。</summary>
public sealed class ProductionSampleRepositoryIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly ProductionSampleRepository _repo;

    public ProductionSampleRepositoryIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "prd_repo_test", $"{Guid.NewGuid():N}.db");
        _db = new Db(_dbPath);
        _db.Open().Dispose(); // 触发目录创建
        new DbInitializer(_db).EnsureCreated();
        _repo = new ProductionSampleRepository(_db);
    }

    [Fact]
    public void GetPage_游标分页与日期过滤()
    {
        var today = DateTime.Today;
        for (var i = 1; i <= 45; i++)
        {
            _repo.Insert(new ProductionSample
            {
                SampleId = $"P-{i:000}",
                SampleName = $"生产样 {i}",
                AlloyType = "6xxx",
                Status = ProductionStatus.Normal,
                CreatedTime = i <= 5 ? today.AddDays(-1) : today, // 前 5 条昨日入库
            });
        }

        // 第一页（今日）：20 条（昨日 5 条被日期过滤排除，今日共 40 条）；时间倒序，同时间按 SampleID 倒序
        var page1 = _repo.GetPage(today, null, null, 20);
        Assert.Equal(20, page1.Count);
        Assert.Equal("P-045", page1[0].SampleId);
        Assert.Equal("P-026", page1[^1].SampleId);

        // 下一页：after 游标
        var page2 = _repo.GetPage(today, page1[^1].SampleId, null, 20);
        Assert.Equal(20, page2.Count);
        Assert.Equal("P-025", page2[0].SampleId);
        Assert.Equal("P-006", page2[^1].SampleId);
        Assert.Equal(40, _repo.Count(today));

        // 上一页：before 游标，倒序返回（与正向页一致）
        var back = _repo.GetPage(today, null, page2[0].SampleId, 20);
        Assert.Equal(20, back.Count);
        Assert.Equal("P-045", back[0].SampleId);
        Assert.Equal("P-026", back[^1].SampleId);

        // 昨日日期
        var yesterday = _repo.GetPage(today.AddDays(-1), null, null, 20);
        Assert.Equal(5, yesterday.Count);
        Assert.Equal("P-005", yesterday[0].SampleId);
        Assert.Equal(5, _repo.Count(today.AddDays(-1)));
    }

    [Fact]
    public void InsertResult_与GetResults_往返一致()
    {
        var now = DateTime.Parse("2026-11-10 09:30:00");
        _repo.Insert(new ProductionSample { SampleId = "P-001", SampleName = "生产样 1", Status = ProductionStatus.Completed, CreatedTime = now });
        _repo.InsertResult(new AnalysisResult
        {
            SampleId = "P-001",
            RawIntensity = "{\"Si\":1234.5}",
            MeasuredConc = 0.12,
            CorrectedConc = 0.119,
            CoeffVersion = 7,
            IsPass = true,
            ResultStatus = AnalysisResultStatus.Success,
            ArchivePosition = "A-03-1",
            OperationTime = now.AddMinutes(5),
        });
        _repo.InsertResult(new AnalysisResult
        {
            SampleId = "P-001",
            MeasuredConc = 0.13,
            CorrectedConc = 0.128,
            ResultStatus = AnalysisResultStatus.Warning,
            IsPass = false,
            OperationTime = now.AddMinutes(9),
        });

        var results = _repo.GetResults("P-001");
        Assert.Equal(2, results.Count);                          // 时间倒序
        Assert.Equal(AnalysisResultStatus.Warning, results[0].ResultStatus);
        Assert.False(results[0].IsPass);
        Assert.Null(results[0].ArchivePosition);
        Assert.Equal("{\"Si\":1234.5}", results[1].RawIntensity);
        Assert.Equal(0.119, results[1].CorrectedConc!.Value, 3);
        Assert.Equal(7, results[1].CoeffVersion);
        Assert.Empty(_repo.GetResults("P-404"));                 // 无结果样品返回空
    }

    public void Dispose()
    {
        try
        {
            // 连接池（Pooling=true）会持有文件句柄，先清池再删临时目录（否则 IOException）
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
