using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.SctStrategy;
using SpectroscopicAnalysis.Host.Data.Repositories;
using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Tests;

/// <summary>ErrorCollector 内存报警表测试：Collect 计数 → 确认清零。</summary>
public sealed class ErrorCollectorTests
{
    [Fact]
    public void Collect_后未确认计数递增且快照最新在前()
    {
        var collector = new ErrorCollector(new SystemLogCollector());

        collector.Collect(ErrorCodes.UnhandledException, "错误A", "S1");
        collector.Collect(ErrorCodes.EmergencyStop, "错误B");

        Assert.Equal(2, collector.UnacknowledgedCount);
        var alarms = collector.GetAlarms();
        Assert.Equal(2, alarms.Count);
        Assert.Equal("错误B", alarms[0].Message);       // 最新在前
        Assert.Equal("S1", alarms[1].SampleId);
        Assert.All(alarms, a => Assert.False(a.IsAcknowledged));
    }

    [Fact]
    public void Acknowledge_单条确认后计数递减()
    {
        var collector = new ErrorCollector(new SystemLogCollector());
        collector.Collect(ErrorCodes.UnhandledException, "错误A");
        collector.Collect(ErrorCodes.UnhandledException, "错误B");

        var first = collector.GetAlarms()[0];
        collector.Acknowledge(first.AlarmId);

        Assert.Equal(1, collector.UnacknowledgedCount);
        Assert.True(collector.GetAlarms().First(a => a.AlarmId == first.AlarmId).IsAcknowledged);
    }

    [Fact]
    public void AcknowledgeAll_全部确认后未确认计数归零()
    {
        var collector = new ErrorCollector(new SystemLogCollector());
        collector.Collect(ErrorCodes.UnhandledException, "错误A");
        collector.Collect(ErrorCodes.UnhandledException, "错误B");

        collector.AcknowledgeAll();

        Assert.Equal(0, collector.UnacknowledgedCount);
    }

    [Fact]
    public async Task 报警表封顶后淘汰最旧()
    {
        var collector = new ErrorCollector(new SystemLogCollector());
        for (var i = 0; i < 501; i++)
        {
            collector.Collect(ErrorCodes.UnhandledException, $"错误{i}");
            await Task.Yield(); // 让出线程，模拟非同栈连续触发
        }

        var alarms = collector.GetAlarms();
        Assert.Equal(500, alarms.Count);
        Assert.Equal("错误500", alarms[0].Message); // 最新保留
    }
}

/// <summary>SctStrategyEngine.GetControlCountdown 测试：ByCount/ByTime 两模式快照与计数流转。</summary>
public sealed class SctControlCountdownTests
{
    /// <summary>可拨动的假时钟（与 SctStrategyEngineTests 同款，避免引入新测试依赖包）。</summary>
    private sealed class SettableTimeProvider(DateTime start) : TimeProvider
    {
        private DateTimeOffset _now = new(start, TimeSpan.Zero);

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static SctSample MakeSample(string id, string type) => new()
    {
        SampleId = id,
        SampleName = id,
        SampleType = type,
        Status = SampleStatus.Normal,
        MaxUsageCount = 50,
        CurrentThickness = 10,
        MinThickness = 5,
        IsActive = true,
    };

    private readonly FakeSctPolicyStore _policy = new();
    private readonly FakeSampleRepository _samples = new();
    private readonly FakeOperationLogRepository _logs = new();
    private readonly FakeErrorCollector _errors = new();
    private readonly SettableTimeProvider _time = new(new DateTime(2026, 11, 18, 8, 0, 0));

    private SctStrategyEngine Create() => new(_policy, _samples, _logs, _errors, _time);

    [Fact]
    public void ByCount_完成生产样后计数递增且到阈值后归零()
    {
        _samples.Store.Add(MakeSample("CTL-001", SampleType.Control));
        _policy.Current.ControlInsertMode = ControlInsertMode.ByCount;
        _policy.Current.ControlInsertCount = 3;
        var engine = Create();

        var countdown = engine.GetControlCountdown();
        Assert.Equal(ControlInsertMode.ByCount, countdown.Mode);
        Assert.Equal(0, countdown.ProductionSinceControl);
        Assert.Equal(3, countdown.ControlInsertCount);

        _samples.Store.Add(MakeSample("P-001", SampleType.Production));
        engine.NotifyAnalysisCompleted("P-001");
        _samples.Store.Add(MakeSample("P-002", SampleType.Production));
        engine.NotifyAnalysisCompleted("P-002");
        Assert.Equal(2, engine.GetControlCountdown().ProductionSinceControl);

        // 第 3 个达阈值 → 触发检查样任务并重置计数
        _samples.Store.Add(MakeSample("P-003", SampleType.Production));
        engine.NotifyAnalysisCompleted("P-003");
        Assert.Equal(0, engine.GetControlCountdown().ProductionSinceControl);
    }

    [Fact]
    public void ByTime_无上次检查记录时返回满间隔()
    {
        _policy.Current.ControlInsertMode = ControlInsertMode.ByTime;
        _policy.Current.ControlInsertMinutes = 60;
        var engine = Create();

        var countdown = engine.GetControlCountdown();
        Assert.Equal(ControlInsertMode.ByTime, countdown.Mode);
        Assert.Equal(60, countdown.MinutesSinceControl);
        Assert.Equal(60, countdown.ControlInsertMinutes);
    }

    [Fact]
    public void ByTime_检查样完成检查后返回已过分钟()
    {
        _samples.Store.Add(MakeSample("CTL-001", SampleType.Control));
        _policy.Current.ControlInsertMode = ControlInsertMode.ByTime;
        _policy.Current.ControlInsertMinutes = 60;
        var engine = Create();

        // 检查样分析完成 → _lastControlCheckTime = 当前时刻
        engine.NotifyAnalysisCompleted("CTL-001");

        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(20, engine.GetControlCountdown().MinutesSinceControl, 1);
    }
}
