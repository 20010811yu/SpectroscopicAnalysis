using CommunityToolkit.Mvvm.ComponentModel;
using SpectroscopicAnalysis.Host.Data.Models;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.SampleManager;
using SpectroscopicAnalysis.Host.SctStrategy;
using SpectroscopicAnalysis.Host.Ui;
using SpectroscopicAnalysis.Host.ViewModels;

namespace SpectroscopicAnalysis.Tests;

/// <summary>Fake 仓储：返回预设列表（不做真实 I/O）。供多个 ViewModel 测试类共用。</summary>
internal sealed class FakeSampleRepository : ISctSampleRepository
{
    public List<SctSample> Store { get; } = new();

    /// <summary>DeleteWithChildren 调用记录（供断言）。</summary>
    public List<string> DeletedWithChildren { get; } = new();

    /// <summary>Update 调用记录（供维护写库断言）。</summary>
    public List<string> Updated { get; } = new();

    public SctSample? GetById(string sampleId) => Store.FirstOrDefault(s => s.SampleId == sampleId);

    public IReadOnlyList<SctSample> GetAll() =>
        Store.OrderByDescending(s => s.LastUseTime)
             .ThenByDescending(s => s.LastHeightCheckTime)
             .ThenBy(s => s.SampleId, StringComparer.Ordinal)
             .ToList();

    public void Insert(SctSample sample) => Store.Add(sample);

    public void Update(SctSample sample)
    {
        Updated.Add(sample.SampleId);
        var idx = Store.FindIndex(s => s.SampleId == sample.SampleId);
        if (idx >= 0)
        {
            Store[idx] = sample;
        }
    }

    public void Delete(string sampleId) { }

    public void DeleteWithChildren(string sampleId)
    {
        DeletedWithChildren.Add(sampleId);
        Store.RemoveAll(s => s.SampleId == sampleId);
    }

    /// <summary>激发后持久化（与真实仓储语义一致：次数+1、厚度、状态、上次使用时间）。</summary>
    public void UpdateAfterSpark(string sampleId, double currentThickness, string status, DateTime usedAt)
    {
        var sample = Store.FirstOrDefault(s => s.SampleId == sampleId);
        if (sample is null)
        {
            return;
        }

        sample.UsageCount++;
        sample.CurrentThickness = currentThickness;
        sample.Status = status;
        sample.LastUseTime = usedAt;
        SparkedIds.Add(sampleId);
    }

    /// <summary>UpdateAfterSpark 调用记录（编排器激发落库断言用）。</summary>
    public List<string> SparkedIds { get; } = new();
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

    public void Alert(string title, string message) => Messages.Add($"{title}|{message}");

    /// <summary>注册弹窗调用记录（主控台注册入口测试断言用）。</summary>
    public List<SampleRegisterFormViewModel> RegisterForms { get; } = new();

    /// <summary>注册弹窗预设返回（true = 用户确认入库成功）。</summary>
    public bool RegisterResult { get; set; } = true;

    /// <summary>弹窗显示期间的模拟交互（真实弹窗为模态，确认发生在返回之前；测试在此回调中 await 表单命令）。</summary>
    public Func<SampleRegisterFormViewModel, Task>? OnShown { get; set; }

    public bool ShowSampleRegister(SampleRegisterFormViewModel form)
    {
        RegisterForms.Add(form);
        // 模拟模态：同步等待弹窗内交互完成后再返回（与真实 ShowDialog 语义一致）
        OnShown?.Invoke(form).GetAwaiter().GetResult();
        return RegisterResult;
    }
}

/// <summary>Fake 错误收集：记录 (错误码, 消息, 样品) 供断言，不做 I/O；内存报警表逻辑复用真实实现。</summary>
internal sealed class FakeErrorCollector : IErrorCollector
{
    /// <summary>真实实现承载报警表（计数/确认逻辑与生产一致）。</summary>
    private readonly ErrorCollector _inner = new(new SystemLogCollector());

    public List<(int Code, string Message, string? SampleId)> Collected { get; } = new();

    public void Collect(int errorCode, string message, string? sampleId = null, Exception? exception = null)
    {
        Collected.Add((errorCode, message, sampleId));
        _inner.Collect(errorCode, message, sampleId, exception);
        AlarmRaised?.Invoke();   // 与真实实现语义一致：报警产生即触发事件
    }

    /// <inheritdoc />
    public event Action? AlarmRaised;

    public int UnacknowledgedCount => _inner.UnacknowledgedCount;

    public IReadOnlyList<AlarmRecord> GetAlarms() => _inner.GetAlarms();

    public void Acknowledge(long alarmId) => _inner.Acknowledge(alarmId);

    public void AcknowledgeAll() => _inner.AcknowledgeAll();
}

/// <summary>Fake 样品注册服务：记录提交的表单，返回可预设的错误（默认成功）。</summary>
internal sealed class FakeSampleRegistrationService : ISampleRegistrationService
{
    public List<SampleRegisterForm> Submitted { get; } = new();

    /// <summary>预设返回错误（null = 成功）。</summary>
    public string? NextError { get; set; }

    public Task<string?> RegisterAsync(SampleRegisterForm form)
    {
        Submitted.Add(form);
        return Task.FromResult(NextError);
    }
}

/// <summary>Fake 标准值仓储：预设列表 + Delete 调用记录。</summary>
internal sealed class FakeCertifiedValueRepository : ISctCertifiedValueRepository
{
    public List<SctCertifiedValue> Store { get; } = new();
    public List<long> Deleted { get; } = new();

    public IReadOnlyList<SctCertifiedValue> GetAll() => Store.ToList();
    public IReadOnlyList<SctCertifiedValue> GetBySampleId(string sampleId) =>
        Store.Where(v => v.SampleId == sampleId).OrderBy(v => v.Element).ToList();
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

    public IReadOnlyList<CorrectionCoefficient> GetAll() =>
        Store.OrderByDescending(c => c.EffectiveTime, StringComparer.Ordinal)
             .ThenBy(c => c.MethodId, StringComparer.Ordinal)
             .ThenBy(c => c.Element, StringComparer.Ordinal)
             .ThenByDescending(c => c.Version)
             .ToList();
    public void Delete(long coeffId) { Deleted.Add(coeffId); Store.RemoveAll(c => c.CoeffId == coeffId); }
}

/// <summary>Fake 操作日志仓储：预设列表 + Delete 调用记录。</summary>
internal sealed class FakeOperationLogRepository : ISctOperationLogRepository
{
    public List<SctOperationLog> Store { get; } = new();
    public List<long> Deleted { get; } = new();

    /// <summary>Insert 调用记录（供审计写入断言）。</summary>
    public List<SctOperationLog> Inserted { get; } = new();

    public IReadOnlyList<SctOperationLog> GetAll() =>
        Store.OrderByDescending(l => l.OperationTime, StringComparer.Ordinal).ToList();
    public IReadOnlyList<SctOperationLog> GetRecentBySampleId(string sampleId, int limit) =>
        Store.Where(l => l.SampleId == sampleId)
             .OrderByDescending(l => l.OperationTime, StringComparer.Ordinal)
             .Take(limit)
             .ToList();
    public void Insert(SctOperationLog log) { Inserted.Add(log); log.LogId = Store.Count + 1; Store.Add(log); }
    public void Delete(long logId) { Deleted.Add(logId); Store.RemoveAll(l => l.LogId == logId); }
}

/// <summary>Fake SCT 策略配置存储：内存字典模拟 JSON 文件往返（不落盘）。</summary>
internal sealed class FakeSctPolicyStore : ISctPolicyStore
{
    public SctPolicyOptions? LastSaved { get; private set; }

    public SctPolicyOptions Current { get; set; } = new();

    public SctPolicyOptions Load() => Current;

    public void Save(SctPolicyOptions options)
    {
        LastSaved = options;
        Current = options;
    }
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
        return new SamplesViewModel(repo, new FakeProductionSampleRepository(), new FakeCertifiedValueRepository(), new FakeControlLimitRepository(),
            new FakeCoefficientRepository(), new FakeOperationLogRepository(), new FakeSctStrategyEngine(),
            new FakeUiDispatcher(), dialogs, new FakeErrorCollector());
    }

    [Fact]
    public async Task Load_台账含各类型SCT样品且可按类型筛选()
    {
        // 生产样已独立建档（Production_Sample），SCT 台账仅承载 SettingUp/Control/TypeStd
        var vm = CreateWith(
            Make("SUS-001", SampleType.SettingUp),
            Make("CTL-002", SampleType.Control, SampleStatus.NeedMeasure),
            Make("TS-6061-01", SampleType.TypeStd),
            Make("P-20261110-07", SampleType.Production));

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.SctSamples.Count);                    // 仓储返回的行全部展示
        vm.SetFilterCommand.Execute(SampleType.Control);
        var filtered = vm.SctSamples.ToList();
        Assert.Single(filtered);
        Assert.Equal("CTL-002", filtered[0].SampleId);
        Assert.Equal("检查样", filtered[0].TypeDisplay);
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

        Assert.Equal("控样", sus.TypeDisplay);
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
    public async Task SetFilter_全部时清空搜索框并显示所有数据()    {
        var vm = CreateWith(
            Make("SUS-001", SampleType.SettingUp),
            Make("CTL-002", SampleType.Control),
            Make("TS-6061-01", SampleType.TypeStd));
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SearchText = "CTL";
        vm.SearchCommand.Execute(null);     // 搜索过滤后仅剩 1 条
        Assert.Single(vm.SctSamples);

        vm.SetFilterCommand.Execute(SamplesViewModel.FilterAll);

        Assert.Equal(string.Empty, vm.SearchText);      // 搜索框已清空
        Assert.False(vm.HasSearchText);                 // 水印恢复
        Assert.Equal(3, vm.SctSamples.Count);           // 表格显示所有数据
    }

    [Fact]
    public async Task SampleDataChanged_通知后台账即时重载()
    {
        var notifier = new SampleDataChangeNotifier();
        var samples = new FakeSampleRepository();
        samples.Store.Add(Make("SUS-001", SampleType.SettingUp));
        var vm = new SamplesViewModel(samples, new FakeProductionSampleRepository(), new FakeCertifiedValueRepository(),
            new FakeControlLimitRepository(), new FakeCoefficientRepository(), new FakeOperationLogRepository(),
            new FakeSctStrategyEngine(), new FakeUiDispatcher(), new FakeDialogService(), new FakeErrorCollector(), notifier);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("9.8 / 12.0", vm.SctSamples.Single(r => r.SampleId == "SUS-001").ThicknessText);

        // 模拟 SCT 管理页维护操作/任务回喂直接改库后发布通知（不经本页导航）
        samples.Store.Single(s => s.SampleId == "SUS-001").CurrentThickness = 7.7;
        notifier.NotifySampleDataChanged();

        // 通知触发的重载在后台线程执行，轮询等待其落回 UI（与真实异步刷新语义一致）
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && !vm.SctSamples.Any(r => r.ThicknessText.StartsWith("7.7")))
        {
            await Task.Delay(10);
        }

        Assert.Equal("7.7 / 12.0", vm.SctSamples.Single(r => r.SampleId == "SUS-001").ThicknessText);
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
            Make("TS-6061-01", SampleType.TypeStd));
        await vm.LoadCommand.ExecuteAsync(null);

        var row = vm.SctSamples.First(r => r.SampleId == "SUS-001");
        await vm.DeleteCommand.ExecuteAsync(row);

        Assert.Single(dialogs.Messages);                    // 弹窗确认已弹出且携带样品上下文
        Assert.Contains("SUS-001", dialogs.Messages[0]);
        Assert.Single(vm.SctSamples);                       // 被删行已移除
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
    public async Task Delete_多行样品可逐行删除()
    {
        var dialogs = new FakeDialogService();
        var vm = CreateWith(dialogs, Make("SUS-001", SampleType.SettingUp), Make("TS-6061-01", SampleType.TypeStd));
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.DeleteCommand.ExecuteAsync(vm.SctSamples[0]);

        Assert.Single(vm.SctSamples);                       // 删除后剩余一行
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
            MakeNamed("SUS-001", "控样 1", SampleType.SettingUp),
            MakeNamed("CTL-002", "检查样 2", SampleType.Control));
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
            MakeNamed("SUS-001", "控样 1", SampleType.SettingUp),
            MakeNamed("SUS-002", "控样 2", SampleType.SettingUp),
            MakeNamed("CTL-001", "检查样 1", SampleType.Control));
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SetFilterCommand.Execute(SampleType.SettingUp);
        vm.SearchText = "1";
        vm.SearchCommand.Execute(null);

        Assert.Single(vm.SctSamples);                       // 控样 ∩ "1" → 仅 SUS-001
        Assert.Equal("SUS-001", vm.SctSamples[0].SampleId);
    }

    [Fact]
    public async Task Search_无命中_保留原数据并弹窗提示()
    {
        var dialogs = new FakeDialogService();
        var vm = CreateWith(dialogs, MakeNamed("SUS-001", "控样 1", SampleType.SettingUp));
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SearchText = "ZZZ";
        vm.SearchCommand.Execute(null);

        Assert.Single(vm.SctSamples);                       // 无命中保留当前数据页（不清空）
        Assert.Equal(1, vm.TotalCount);
        Assert.Single(dialogs.Messages);                    // 弹窗提示未找到数据
        Assert.Contains("未找到数据", dialogs.Messages[0]);
    }

    // ==================== 分页（每页 15 条，行高统一完整展示） ====================

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
        Assert.Equal(SamplesViewModel.PageSize, vm.SctSamples.Count);   // 第 1 页满 15 条
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
        Assert.Equal("S-016", vm.SctSamples[0].SampleId);   // 每页 15 条，第 2 页从 S-016 起
        Assert.True(vm.HasPrevPage && vm.HasNextPage);

        vm.NextPageCommand.Execute(null);
        Assert.Equal(3, vm.CurrentPage);
        Assert.Equal(15, vm.SctSamples.Count);              // 末页 45 - 30 = 15 条（满页）
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

        vm.NextPageCommand.Execute(null);
        Assert.Equal(6, vm.SctSamples.Count);               // 第 2 页（末页）45→21 条，剩 21 - 15 = 6 条
        await vm.DeleteCommand.ExecuteAsync(vm.SctSamples[0]);

        Assert.Equal(2, vm.CurrentPage);                    // 删除后 20 条共 2 页 → 保持末页（第 2 页，5 条）
        Assert.Equal(5, vm.SctSamples.Count);
        Assert.Equal(20, vm.TotalCount);
    }

    [Fact]
    public async Task Paging_页码跳转_合法页码直达并清空输入()
    {
        var vm = CreateWith(MakeMany(45).ToArray());
        await vm.LoadCommand.ExecuteAsync(null);

        vm.PageJumpInput = "3";
        vm.JumpToPageCommand.Execute(null);
        Assert.Equal(3, vm.CurrentPage);
        Assert.Equal("S-031", vm.SctSamples[0].SampleId);   // 第 3 页从 S-031 起
        Assert.Equal(string.Empty, vm.PageJumpInput);       // 跳转后清空输入框
    }

    [Fact]
    public async Task Paging_页码跳转_非法输入忽略()
    {
        var vm = CreateWith(MakeMany(45).ToArray());
        await vm.LoadCommand.ExecuteAsync(null);

        vm.PageJumpInput = "abc";
        vm.JumpToPageCommand.Execute(null);
        Assert.Equal(1, vm.CurrentPage);                    // 非法输入不跳转
        Assert.Equal("abc", vm.PageJumpInput);              // 输入保留便于修正
    }

    [Fact]
    public async Task Paging_页码跳转_越界收敛到末页()
    {
        var vm = CreateWith(MakeMany(45).ToArray());
        await vm.LoadCommand.ExecuteAsync(null);

        vm.PageJumpInput = "999";
        vm.JumpToPageCommand.Execute(null);
        Assert.Equal(3, vm.TotalPages);
        Assert.Equal(3, vm.CurrentPage);                    // 越界收敛到末页
        Assert.Equal(15, vm.SctSamples.Count);
    }

    [Fact]
    public async Task Search_无命中_保留当前数据页并弹窗提示()
    {
        var dialogs = new FakeDialogService();
        var vm = CreateWith(dialogs, MakeMany(45).ToArray());
        await vm.LoadCommand.ExecuteAsync(null);
        vm.NextPageCommand.Execute(null);
        Assert.Equal(2, vm.CurrentPage);
        var before = vm.SctSamples.Select(r => r.SampleId).ToList();

        vm.SearchText = "不存在的关键词XYZ";
        vm.SearchCommand.Execute(null);

        Assert.Equal(2, vm.CurrentPage);                    // 保留原数据页
        Assert.Equal(before, vm.SctSamples.Select(r => r.SampleId).ToList());   // 数据未清空
        Assert.Equal(45, vm.TotalCount);                    // 分页状态未变
        Assert.Equal(3, vm.TotalPages);
        Assert.Single(dialogs.Messages);                    // 弹窗提示一次
        Assert.Contains("未找到数据", dialogs.Messages[0]);
    }

    [Fact]
    public async Task Search_有命中_回到第1页且不弹窗()
    {
        var dialogs = new FakeDialogService();
        var vm = CreateWith(dialogs, MakeMany(45).ToArray());
        await vm.LoadCommand.ExecuteAsync(null);
        vm.NextPageCommand.Execute(null);

        vm.SearchText = "04";
        vm.SearchCommand.Execute(null);

        Assert.Equal(1, vm.CurrentPage);                    // 正常回第 1 页
        Assert.Equal(7, vm.TotalCount);
        Assert.Empty(dialogs.Messages);                     // 命中不弹窗
    }

    [Fact]
    public async Task Search_关键词为空_正常全量刷新()
    {
        var dialogs = new FakeDialogService();
        var vm = CreateWith(dialogs, MakeMany(45).ToArray());
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SearchText = string.Empty;
        vm.SearchCommand.Execute(null);

        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal(45, vm.TotalCount);
        Assert.Equal(15, vm.SctSamples.Count);
        Assert.Empty(dialogs.Messages);
    }
}

