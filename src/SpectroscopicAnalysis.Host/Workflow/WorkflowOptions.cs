namespace SpectroscopicAnalysis.Host.Workflow;

/// <summary>
/// Workflow 集中配置（开发规范 11.3/第 6 节：超时、重试、轮询周期禁止散落硬编码）。
/// appsettings.json "Workflow" 节绑定；缺省值即 Stub 阶段默认。
/// </summary>
public sealed class WorkflowOptions
{
    /// <summary>单步设备调用超时（毫秒）。</summary>
    public int StepTimeoutMs { get; set; } = 30_000;

    /// <summary>设备调用重试上限（耗尽走报警链路，不自动无限重试）。</summary>
    public int StepRetryCount { get; set; } = 3;

    /// <summary>锁获取超时（毫秒；获取失败任务排队等待，不强制抢占）。</summary>
    public int LockTimeoutMs { get; set; } = 5_000;

    /// <summary>主循环轮询间隔（毫秒）。</summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>任务间隔（毫秒）：上一任务完成后到下一任务出队前的等待，模拟真实执行节拍；仅队列仍有排队任务时生效。</summary>
    public int TaskIntervalMs { get; set; } = 60_000;

    /// <summary>策略引擎时钟节拍间隔（毫秒；定时/按时间触发判定）。</summary>
    public int EngineTickIntervalMs { get; set; } = 60_000;
}
