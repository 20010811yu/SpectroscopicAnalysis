using SpectroscopicAnalysis.Host.Workflow;

namespace SpectroscopicAnalysis.Tests;

/// <summary>
/// Workflow 状态机转移表穷举测试（开发规范第 9 节必测核心；编排设计文档 §12）：
/// 合法链全通、所有非法跳跃被拒、非终态可达 Completed、失败分支完整。
/// </summary>
public sealed class WorkflowStatesTests
{
    private static readonly string[] TaskStates =
    [
        TaskState.Idle, TaskState.Registered, TaskState.Preparing, TaskState.Milling,
        TaskState.Placing, TaskState.Analyzing, TaskState.Archiving,
        TaskState.Completed, TaskState.Failed,
    ];

    private static readonly (string From, string To)[] LegalChain =
    [
        (TaskState.Idle, TaskState.Registered),
        (TaskState.Registered, TaskState.Preparing),
        (TaskState.Preparing, TaskState.Milling),
        (TaskState.Milling, TaskState.Placing),
        (TaskState.Placing, TaskState.Analyzing),
        (TaskState.Analyzing, TaskState.Archiving),
        (TaskState.Archiving, TaskState.Completed),
    ];

    [Fact]
    public void 合法链全部允许()
    {
        foreach (var (from, to) in LegalChain)
        {
            Assert.True(TaskState.CanTransition(from, to), $"{from} → {to} 应合法");
        }
    }

    [Fact]
    public void 任意非终态可直接转Failed()
    {
        foreach (var state in TaskStates.Where(s => s is not (TaskState.Completed or TaskState.Failed)))
        {
            Assert.True(TaskState.CanTransition(state, TaskState.Failed), $"{state} 应可转 Failed");
        }
    }

    [Fact]
    public void 非法跳跃全部拒绝()
    {
        var legalPairs = LegalChain.ToHashSet();
        var legalFailed = TaskStates
            .Where(s => s is not (TaskState.Completed or TaskState.Failed))
            .Select(s => (s, TaskState.Failed))
            .ToHashSet();

        foreach (var from in TaskStates)
        {
            foreach (var to in TaskStates)
            {
                if (from == to)
                {
                    continue;
                }

                var legal = legalPairs.Contains((from, to)) || legalFailed.Contains((from, to));
                Assert.Equal(legal, TaskState.CanTransition(from, to));
            }
        }
    }

    [Fact]
    public void 终态无出口()
    {
        Assert.False(TaskState.CanTransition(TaskState.Completed, TaskState.Idle));
        Assert.False(TaskState.CanTransition(TaskState.Failed, TaskState.Idle));
    }

    [Fact]
    public void 启动自检_转移表完整()
    {
        // 转移表定义错误时抛 InvalidOperationException（编排设计文档 §4）
        TaskState.ValidateTransitions();
    }

    [Fact]
    public void SCT子状态链覆盖任务类型差异()
    {
        // 标准化含 UpdatingCoeff；控制检查不含；测高走 MeasuringHeight 且不激发
        Assert.Contains(SctSubState.UpdatingCoeff, SctSubState.StandardizeChain);
        Assert.DoesNotContain(SctSubState.UpdatingCoeff, SctSubState.ControlCheckChain);
        Assert.Contains(SctSubState.MeasuringHeight, SctSubState.HeightMeasureChain);
        Assert.DoesNotContain(SctSubState.Analyzing, SctSubState.HeightMeasureChain);
    }
}
