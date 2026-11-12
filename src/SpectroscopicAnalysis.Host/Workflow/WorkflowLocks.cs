namespace SpectroscopicAnalysis.Host.Workflow;

/// <summary>资源互斥锁名称（任务清单第十节：RobotLock/SparkStandLock/CoefficientLock/SampleArchiveLock）。</summary>
public static class WorkflowLockName
{
    public const string Robot = "RobotLock";
    public const string SparkStand = "SparkStandLock";
    public const string SampleArchive = "SampleArchiveLock";
    public const string Coefficient = "CoefficientLock";
}

/// <summary>
/// 资源互斥锁组（进程内 SemaphoreSlim 封装，单机上位机无需分布式）。
/// 带超时获取；获取失败由调用方排队等待，不强制抢占（编排设计文档 §7.1）。
/// </summary>
public sealed class WorkflowLocks
{
    private readonly Dictionary<string, SemaphoreSlim> _locks = new()
    {
        [WorkflowLockName.Robot] = new SemaphoreSlim(1, 1),
        [WorkflowLockName.SparkStand] = new SemaphoreSlim(1, 1),
        [WorkflowLockName.SampleArchive] = new SemaphoreSlim(1, 1),
    };

    /// <summary>各锁当前占用状态（快照供 UI/诊断）。</summary>
    public IReadOnlyDictionary<string, bool> Occupancy =>
        _locks.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentCount == 0);

    /// <summary>持锁执行：超时获取失败抛 TimeoutException（调用方转任务失败 + 错误链）。</summary>
    public async Task<T> RunWithLockAsync<T>(string lockName, int timeoutMs, Func<Task<T>> action, CancellationToken ct)
    {
        var semaphore = _locks[lockName];
        if (!await semaphore.WaitAsync(timeoutMs, ct))
        {
            throw new TimeoutException($"获取 {lockName} 超时（{timeoutMs}ms）");
        }

        try
        {
            return await action();
        }
        finally
        {
            semaphore.Release();
        }
    }
}
