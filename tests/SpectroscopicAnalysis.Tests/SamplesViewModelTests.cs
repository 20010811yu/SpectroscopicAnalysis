using CommunityToolkit.Mvvm.ComponentModel;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.Ui;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>Fake 仓储：返回预设列表（不做真实 I/O）。供多个 ViewModel 测试类共用。</summary>
internal sealed class FakeSampleRepository : ISctSampleRepository
{
    public List<SctSample> Store { get; } = new();

    /// <summary>DeleteWithChildren 调用记录（供断言）。</summary>
    public List<string> DeletedWithChildren { get; } = new();

    public SctSample? GetById(string sampleId) => Store.FirstOrDefault(s => s.SampleId == sampleId);

    public IReadOnlyList<SctSample> GetAll() => Store.ToList();

    public void Insert(SctSample sample) => Store.Add(sample);

    public void Update(SctSample sample) { }

    public void Delete(string sampleId) { }

    public void DeleteWithChildren(string sampleId)
    {
        DeletedWithChildren.Add(sampleId);
        Store.RemoveAll(s => s.SampleId == sampleId);
    }

    public void UpdateAfterSpark(string sampleId, double currentThickness, string status, DateTime usedAt) { }

    public IReadOnlyList<SctSample> GetProductionPage(DateTime? createdOn, string? afterSampleId, string? beforeSampleId, int pageSize)
    {
        var query = Store.Where(s => s.SampleType == SampleType.Production);
        if (createdOn is not null)
        {
            var day = createdOn.Value.Date;
            query = query.Where(s => s.CreatedTime >= day && s.CreatedTime < day.AddDays(1));
        }

        if (afterSampleId is not null)
        {
            query = query.Where(s => string.CompareOrdinal(s.SampleId, afterSampleId) > 0);
        }

        if (beforeSampleId is not null)
        {
            query = query.Where(s => string.CompareOrdinal(s.SampleId, beforeSampleId) < 0);
        }

        var ordered = query.OrderBy(s => s.SampleId, StringComparer.Ordinal).ToList();
        if (beforeSampleId is not null)
        {
            // 与真实 SQL（DESC LIMIT 后反转）等价：游标前的最后 pageSize 条，保持升序返回
            return ordered.TakeLast(pageSize).ToList();
        }

        return ordered.Take(pageSize).ToList();
    }

    public int CountProduction(DateTime? createdOn)
    {
        var query = Store.Where(s => s.SampleType == SampleType.Production);
        if (createdOn is not null)
        {
            var day = createdOn.Value.Date;
            query = query.Where(s => s.CreatedTime >= day && s.CreatedTime < day.AddDays(1));
        }

        return query.Count();
    }
}

/// <summary>Fake 调度器：同步执行（保证测试确定性）。</summary>
internal sealed class FakeUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action) => action();

    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
}

/// <summary>Fake 确认弹窗：返回预设结果（默认确认），记录弹窗内容供断言。</summary>
internal sealed class FakeDialogService : IDialogService
{
    /// <summary>下次 Confirm 的返回值（默认 true = 确认）。</summary>
    public bool Result { get; set; } = true;

    public List<string> Messages { get; } = new();

    public bool Confirm(string title, string message)
    {
        Messages.Add($"{title}|{message}");
        return Result;
    }
}

/// <summary>Fake 错误收集：记录 (错误码, 消息, 样品) 供断言，不做 I/O。</summary>
internal sealed class FakeErrorCollector : IErrorCollector
{
    public List<(int Code, string Message, string? SampleId)> Collected { get; } = new();

    public void Collect(int errorCode, string message, string? sampleId = null, Exception? exception = null) =>
        Collected.Add((errorCode, message, sampleId));
}

/// <summary>Fake 标准值仓储：预设列表 + Delete 调用记录。</summary>
internal sealed class FakeCertifiedValueRepository : ISctCertifiedValueRepository
{
    public List<SctCertifiedValue> Store { get; } = new();
    public List<long> Deleted { get; } = new();

    public IReadOnlyList<SctCertifiedValue> GetAll() => Store.ToList();
    public void Delete(long id) { Deleted.Add(id); Store.RemoveAll(v => v.Id == id); }
}

/// <summary>Fake 控制限仓储：预设列表 + Delete 调用记录。</summary>
internal sealed class FakeControlLimitRepository : IControlLimitRepository
{
    public List<ControlLimit> Store { get; } = new();
    public List<long> Deleted { get; } = new();

    public IReadOnlyList<ControlLimit> GetAll() => Store.ToList();
    public void Delete(long id) { Deleted.Add(id); Store.RemoveAll(l => l.LimitId == id); }
}

/// <summary>Fake 校正系数仓储：预设列表 + Delete 调用记录。</summary>
internal sealed class FakeCoefficientRepository : ICorrectionCoefficientRepository
{
    public List<CorrectionCoefficient> Store { get; } = new();
    public List<long> Deleted { get; } = new();

    public IReadOnlyList<CorrectionCoefficient> GetAll() => Store.ToList();
    public void Delete(long coeffId) { Deleted.Add(coeffId); Store.RemoveAll(c => c.CoeffId == coeffId); }
}

/// <summary>Fake 操作日志仓储：预设列表 + Delete 调用记录。</summary>
internal sealed class FakeOperationLogRepository : ISctOperationLogRepository
{
    public List<SctOperationLog> Store { get; } = new();
    public List<long> Deleted { get; } = new();

    public IReadOnlyList<SctOperationLog> GetAll() => Store.ToList();
    public void Delete(long logId) { Deleted.Add(logId); Store.RemoveAll(l => l.LogId == logId); }
}

/// <summary>样品档案 ViewModel 测试：数据加载 / 展示映射 / 类型筛选（开发计划 D4 先行项验收）。</summary>
public sealed class SamplesViewModelTests
{

    private static SctSample Make(string id, string type, string status = SampleStatus.Normal) => new()
    {
        SampleId = id,
        SampleName = id,
        SampleType = type,
        AlloyType = "6xxx",
        RackPosition = "A01",
        InitialThickness = 12.0,
        CurrentThickness = 9.8,
        MinThickness = 8.0,
        UsageCount = 50,
        MaxUsageCount = 50,
        Status = status,
        LastHeightCheckTime = new DateTime(2026, 11, 7, 8, 0, 0),
    };

    private static SamplesViewModel CreateWith(params SctSample[] samples) =>
        CreateWith(new FakeDialogService(), samples);

    private static SamplesViewModel CreateWith(FakeDialogService dialogs, params SctSample[] samples)
    {
        var repo = new FakeSampleRepository();
        foreach (var s in samples)
        {
            repo.Store.Add(s);
        }
        return new SamplesViewModel(repo, new FakeCertifiedValueRepository(), new FakeControlLimitRepository(),
            new FakeCoefficientRepository(), new FakeOperationLogRepository(),
            new FakeUiDispatcher(), dialogs, new FakeErrorCollector());
    }

    [Fact]
    public async Task Load_台账含生产样品且可按类型筛选()
    {
        var vm = CreateWith(
            Make("SUS-001", SampleType.SettingUp),
            Make("CTL-002", SampleType.Control, SampleStatus.NeedMeasure),
            Make("TS-6061-01", SampleType.TypeStd),
            Make("P-20261110-07", SampleType.Production));

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.SctSamples.Count);                    // 全部类型均在台账
        vm.SetFilterCommand.Execute(SampleType.Production);
        var filtered = vm.SctSamples.ToList();
        Assert.Single(filtered);
        Assert.Equal("P-20261110-07", filtered[0].SampleId);     // 生产样筛选生效
        Assert.Equal("生产样", filtered[0].TypeDisplay);
    }

    [Fact]
    public async Task Load_展示映射正确()
    {
        var vm = CreateWith(
            Make("SUS-001", SampleType.SettingUp),
            Make("SUS-003", SampleType.SettingUp, SampleStatus.Disabled),
            Make("TS-6061-01", SampleType.TypeStd));

        await vm.LoadCommand.ExecuteAsync(null);
        var sus = vm.SctSamples.First(r => r.SampleId == "SUS-001");

        Assert.Equal("设置样", sus.TypeDisplay);
        Assert.Equal("9.8 / 12.0", sus.ThicknessText);
        Assert.Equal("50/50", sus.UsageText);
        Assert.Equal("11-07 08:00", sus.LastHeightText);
        Assert.Equal("● 正常", sus.StatusText);

        var disabled = vm.SctSamples.First(r => r.SampleId == "SUS-003");
        Assert.Equal("类型标准化样", vm.SctSamples.First(r => r.SampleId == "TS-6061-01").TypeDisplay);
        Assert.Equal("✖ 禁用（过薄）", disabled.StatusText);
    }

    [Fact]
    public async Task SetFilter_按类型过滤()
    {
        var vm = CreateWith(
            Make("SUS-001", SampleType.SettingUp),
            Make("CTL-002", SampleType.Control),
            Make("TS-6061-01", SampleType.TypeStd));
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SetFilterCommand.Execute("Control");
        var filtered = vm.SctSamples.ToList();
        Assert.Single(filtered);
        Assert.Equal("CTL-002", filtered[0].SampleId);

        vm.SetFilterCommand.Execute(SamplesViewModel.FilterAll);
        Assert.Equal(3, vm.SctSamples.Count);
    }

    [Fact]
    public async Task Load_空库时台账为空()
    {
        var vm = CreateWith();
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Empty(vm.SctSamples);
    }

    [Fact]
    public async Task Load_重复调用幂等()
    {
        var vm = CreateWith(Make("SUS-001", SampleType.SettingUp));
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Single(vm.SctSamples);
    }

    [Fact]
    public async Task Delete_弹窗确认后级联删除并从集合移除()
    {
        var dialogs = new FakeDialogService();
        var vm = CreateWith(dialogs,
            Make("SUS-001", SampleType.SettingUp),
            Make("P-20261110-07", SampleType.Production));
        await vm.LoadCommand.ExecuteAsync(null);

        var row = vm.SctSamples.First(r => r.SampleId == "SUS-001");
        await vm.DeleteCommand.ExecuteAsync(row);

        Assert.Single(dialogs.Messages);                    // 弹窗确认已弹出且携带样品上下文
        Assert.Contains("SUS-001", dialogs.Messages[0]);
        Assert.Single(vm.SctSamples);                       // 被删行已移除（仅剩生产样行）
        Assert.DoesNotContain(vm.SctSamples, r => r.SampleId == "SUS-001");
        Assert.Null(vm.DeleteError);
    }

    [Fact]
    public async Task Delete_弹窗取消则不删除()
    {
        var dialogs = new FakeDialogService { Result = false };
        var vm = CreateWith(dialogs, Make("SUS-001", SampleType.SettingUp));
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.DeleteCommand.ExecuteAsync(vm.SctSamples[0]);

        Assert.Single(vm.SctSamples);                       // 行数据保持原样
        Assert.Null(vm.DeleteError);
    }

    [Fact]
    public async Task Delete_生产样品可从台账删除()
    {
        var dialogs = new FakeDialogService();
        var vm = CreateWith(dialogs, Make("P-20261110-07", SampleType.Production));
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.DeleteCommand.ExecuteAsync(vm.SctSamples[0]);

        Assert.Empty(vm.SctSamples);                        // 生产样品删除后从台账移除
        Assert.Null(vm.DeleteError);
    }

    [Fact]
    public async Task Delete_空参数不抛异常()
    {
        var vm = CreateWith(Make("SUS-001", SampleType.SettingUp));
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Single(vm.SctSamples);                       // 状态不变
    }

    // ==================== 查询（关键词模糊匹配编号/名称） ====================

    private static SctSample MakeNamed(string id, string name, string type) => new()
    {
        SampleId = id, SampleName = name, SampleType = type,
        InitialThickness = 12.0, CurrentThickness = 11.0, MinThickness = 8.0,
        UsageCount = 1, MaxUsageCount = 50, Status = SampleStatus.Normal,
    };

    [Fact]
    public async Task Search_按编号命中()
    {
        var vm = CreateWith(
            MakeNamed("SUS-001", "设置样 1", SampleType.SettingUp),
            MakeNamed("CTL-002", "控制样 2", SampleType.Control));
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SearchText = "CTL";
        vm.SearchCommand.Execute(null);

        Assert.Single(vm.SctSamples);
        Assert.Equal("CTL-002", vm.SctSamples[0].SampleId);
        Assert.Equal(1, vm.TotalCount);
    }

    [Fact]
    public async Task Search_按名称命中且大小写不敏感()
    {
        var vm = CreateWith(
            MakeNamed("TS-A-01", "TypeStd-6061", SampleType.TypeStd),
            MakeNamed("TS-B-01", "TypeStd-1050", SampleType.TypeStd));
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SearchText = "6061";
        vm.SearchCommand.Execute(null);

        Assert.Single(vm.SctSamples);
        Assert.Equal("TS-A-01", vm.SctSamples[0].SampleId);
    }

    [Fact]
    public async Task Search_类型筛选与关键词组合过滤()
    {
        var vm = CreateWith(
            MakeNamed("SUS-001", "设置样 1", SampleType.SettingUp),
            MakeNamed("SUS-002", "设置样 2", SampleType.SettingUp),
            MakeNamed("CTL-001", "控制样 1", SampleType.Control));
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SetFilterCommand.Execute(SampleType.SettingUp);
        vm.SearchText = "1";
        vm.SearchCommand.Execute(null);

        Assert.Single(vm.SctSamples);                       // 设置样 ∩ "1" → 仅 SUS-001
        Assert.Equal("SUS-001", vm.SctSamples[0].SampleId);
    }

    [Fact]
    public async Task Search_无命中显示空集()
    {
        var vm = CreateWith(MakeNamed("SUS-001", "设置样 1", SampleType.SettingUp));
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SearchText = "ZZZ";
        vm.SearchCommand.Execute(null);

        Assert.Empty(vm.SctSamples);
        Assert.Equal(0, vm.TotalCount);
        Assert.Equal(1, vm.TotalPages);                     // 至少 1 页
    }

    // ==================== 分页（每页 20 条） ====================

    private static List<SctSample> MakeMany(int count) =>
        Enumerable.Range(1, count)
            .Select(i => MakeNamed($"S-{i:000}", $"样品 {i:000}", SampleType.SettingUp))
            .ToList();

    [Fact]
    public async Task Paging_总数与页数正确且只显示当前页()
    {
        var vm = CreateWith(MakeMany(45).ToArray());
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(45, vm.TotalCount);
        Assert.Equal(3, vm.TotalPages);
        Assert.Equal(SamplesViewModel.PageSize, vm.SctSamples.Count);   // 第 1 页满 20 条
        Assert.Equal("S-001", vm.SctSamples[0].SampleId);
        Assert.False(vm.HasPrevPage);
        Assert.True(vm.HasNextPage);
    }

    [Fact]
    public async Task Paging_翻页与边界禁用()
    {
        var vm = CreateWith(MakeMany(45).ToArray());
        await vm.LoadCommand.ExecuteAsync(null);

        vm.NextPageCommand.Execute(null);
        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal("S-021", vm.SctSamples[0].SampleId);   // 每页 20 条，第 2 页从 S-021 起
        Assert.True(vm.HasPrevPage && vm.HasNextPage);

        vm.NextPageCommand.Execute(null);
        Assert.Equal(3, vm.CurrentPage);
        Assert.Equal(5, vm.SctSamples.Count);               // 末页 45 - 40 = 5 条
        Assert.False(vm.HasNextPage);
        Assert.False(vm.NextPageCommand.CanExecute(null));  // 末页"下一页"禁用

        vm.PrevPageCommand.Execute(null);
        Assert.Equal(2, vm.CurrentPage);

        vm.PrevPageCommand.Execute(null);
        vm.PrevPageCommand.Execute(null);                   // 已在第 1 页，再点不再前翻（CanExecute=false）
        Assert.Equal(1, vm.CurrentPage);
    }

    [Fact]
    public async Task Paging_搜索后回到第1页且页数收敛()
    {
        var vm = CreateWith(MakeMany(45).ToArray());
        await vm.LoadCommand.ExecuteAsync(null);
        vm.NextPageCommand.Execute(null);
        vm.NextPageCommand.Execute(null);
        Assert.Equal(3, vm.CurrentPage);

        // 搜索命中 7 条（S-004、S-040~045）→ 回到第 1 页，总页数变 1
        vm.SearchText = "04";
        vm.SearchCommand.Execute(null);
        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal(7, vm.TotalCount);
        Assert.Equal(1, vm.TotalPages);
    }

    [Fact]
    public async Task Paging_删除末页最后一条后页码回退()
    {
        var dialogs = new FakeDialogService();
        var many = MakeMany(21);
        var vm = CreateWith(dialogs, many.ToArray());
        await vm.LoadCommand.ExecuteAsync(null);

        vm.NextPageCommand.Execute(null);                   // 第 2 页只剩 1 条
        Assert.Single(vm.SctSamples);
        await vm.DeleteCommand.ExecuteAsync(vm.SctSamples[0]);

        Assert.Equal(1, vm.CurrentPage);                    // 末页空 → 回退第 1 页
        Assert.Equal(SamplesViewModel.PageSize, vm.SctSamples.Count);
        Assert.Equal(20, vm.TotalCount);
    }
}

