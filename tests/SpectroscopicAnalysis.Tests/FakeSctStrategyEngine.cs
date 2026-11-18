using SpectroscopicAnalysis.Host.SctStrategy;

namespace SpectroscopicAnalysis.Tests;

/// <summary>Fake SCT 策略引擎：记录 SubmitTask 调用（注册入队断言用），其余成员空实现。</summary>
internal sealed class FakeSctStrategyEngine : ISctStrategyEngine
{
    /// <summary>已提交的 (taskType, sampleId, reason) 记录。</summary>
    public List<(string TaskType, string? SampleId, string Reason)> Submitted { get; } = new();

    public int SubmitTask(string taskType, string? sampleId, string reason)
    {
        Submitted.Add((taskType, sampleId, reason));
        return Submitted.Count;
    }

    public bool TryDequeue(out SctTask task)
    {
        task = null!;
        return false;
    }

    public IReadOnlyList<SctTask> GetQueuedTasks() => Array.Empty<SctTask>();

    public IReadOnlyList<SctTask> GetRecentTasks() => Array.Empty<SctTask>();

    public void CompleteTask(int taskId, bool success, string? error)
    {
    }

    public void NotifyAnalysisCompleted(string sampleId)
    {
    }

    public void NotifyControlResult(string sampleId, bool passed)
    {
    }

    public void NotifyStandardizeCompleted(string sampleId)
    {
    }

    public void NotifyHeightMeasured(string sampleId, double thickness)
    {
    }

    public void NotifyModeChanged(bool autoMode)
    {
    }

    public void NotifyAlloySwitched(string alloyType)
    {
    }

    public void OnTick()
    {
    }

    public void EmergencyReset()
    {
    }

    /// <summary>可预设的倒计时快照（默认按数量模式 0/15）。</summary>
    public SctControlCountdown Countdown { get; set; } = new()
    {
        Mode = ControlInsertMode.ByCount,
        ProductionSinceControl = 0,
        ControlInsertCount = 15,
        MinutesSinceControl = 0,
        ControlInsertMinutes = 60,
    };

    public SctControlCountdown GetControlCountdown() => Countdown;
}
