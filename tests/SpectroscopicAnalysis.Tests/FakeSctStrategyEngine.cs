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

    /// <summary>镜像引擎语义：仅排队中任务可置顶（同档 ManualSortKey 最小键-1 秒）。</summary>
    public bool MoveToTop(int taskId)
    {
        var task = RecentTasks.FirstOrDefault(t => t.TaskId == taskId && t.Status == SctTaskStatus.Queued);
        if (task is null)
        {
            return false;
        }

        var tierMinKey = RecentTasks
            .Where(t => t.Priority == task.Priority)
            .Select(t => t.ManualSortKey ?? t.EnqueueTime)
            .DefaultIfEmpty(task.EnqueueTime)
            .Min();
        task.ManualSortKey = tierMinKey - TimeSpan.FromSeconds(1);
        return true;
    }

    public IReadOnlyList<SctTask> GetQueuedTasks() => RecentTasks
        .Where(t => t.Status == SctTaskStatus.Queued)
        .ToList();

    /// <summary>镜像引擎语义：仅排队中任务可删除（置 Cancelled 留在 RecentTasks 供断言）。</summary>
    public bool RemoveQueuedTask(int taskId)
    {
        var task = RecentTasks.FirstOrDefault(t => t.TaskId == taskId && t.Status == SctTaskStatus.Queued);
        if (task is null)
        {
            return false;
        }

        task.Status = SctTaskStatus.Cancelled;
        return true;
    }

    /// <summary>移除指定样品全部排队任务，RemovedSampleLogIds 记录被删 TaskId 供断言。</summary>
    public int RemoveQueuedTasksBySample(string sampleId)
    {
        var targets = RecentTasks.Where(t => t.Status == SctTaskStatus.Queued &&
                                            string.Equals(t.SampleId, sampleId, StringComparison.Ordinal)).ToList();
        foreach (var task in targets)
        {
            task.Status = SctTaskStatus.Cancelled;
        }

        return targets.Count;
    }

    /// <summary>GetRecentTasks 返回值（测试可预置：验证任务队列展示过滤与排序）。</summary>
    public List<SctTask> RecentTasks { get; } = new();

    public IReadOnlyList<SctTask> GetRecentTasks() => RecentTasks;

    public void CompleteTask(int taskId, bool success, string? error)
    {
    }

    public void NotifyAnalysisCompleted(string sampleId, string? taskType = null)
    {
        AnalysisCompletedIds.Add(sampleId);
    }

    /// <summary>NotifyAnalysisCompleted 调用记录（供编排层回喂断言）。</summary>
    public List<string> AnalysisCompletedIds { get; } = new();

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

    public void UpdateCurrentAlloy(string alloyType)
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

    /// <summary>可预置的标准化期限倒计时快照（默认两个开关均未启用）。</summary>
    public SctStandardizeCountdown StandardizeCountdown { get; set; } = new(
        PeriodicEnabled: false, StandardizeDueTime: null,
        TypeStdEnabled: false, TypeStdDueTime: null);

    public SctStandardizeCountdown GetStandardizeCountdown() => StandardizeCountdown;
}
