namespace SpectroscopicAnalysis.Host.Workflow;

/// <summary>
/// 任务状态机（任务清单第 7 节 P6，命名不得自创——AGENTS 规则 4）：
/// Idle → Registered → Preparing → Milling → Placing → Analyzing → Archiving → Completed / Failed。
/// 转移表硬编码，仅允许顺序转移；任何状态可转 Failed（开发规范 11.3：不允许跳跃转移）。
/// </summary>
public static class TaskState
{
    public const string Idle = "Idle";
    public const string Registered = "Registered";
    public const string Preparing = "Preparing";
    public const string Milling = "Milling";
    public const string Placing = "Placing";
    public const string Analyzing = "Analyzing";
    public const string Archiving = "Archiving";
    public const string Completed = "Completed";
    public const string Failed = "Failed";

    /// <summary>合法转移表（key=当前状态 → 允许的下一状态集合；启动自检见 ValidateTransitions）。</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Transitions = new Dictionary<string, IReadOnlyList<string>>
    {
        [Idle] = [Registered, Failed],
        [Registered] = [Preparing, Failed],
        [Preparing] = [Milling, Failed],
        [Milling] = [Placing, Failed],
        [Placing] = [Analyzing, Failed],
        [Analyzing] = [Archiving, Failed],
        [Archiving] = [Completed, Failed],
        [Completed] = [],
        [Failed] = [],
    };

    /// <summary>转移守卫：目标状态不在合法集合 → 拒绝（调用方记录日志）。</summary>
    public static bool CanTransition(string from, string to) =>
        Transitions.TryGetValue(from, out var next) && next.Contains(to);

    /// <summary>
    /// 启动自检（编排设计文档 §4）：每个状态都有转移定义、非终态可达 Completed、任何状态可达 Failed。
    /// 主流程启动时调用，转移表写错则立即暴露。
    /// </summary>
    public static void ValidateTransitions()
    {
        foreach (var state in Transitions.Keys)
        {
            if (!Transitions.TryGetValue(state, out var next))
            {
                throw new InvalidOperationException($"任务状态 {state} 缺少转移定义");
            }

            if (state is Completed or Failed)
            {
                continue;   // 终态
            }

            // 非终态必须可达 Completed（沿合法转移最多 8 步）
            if (!Reachable(state, Completed))
            {
                throw new InvalidOperationException($"任务状态 {state} 不可达 Completed");
            }

            // 任何非终态必须可直接转 Failed（失败分支完整）
            if (!next.Contains(Failed))
            {
                throw new InvalidOperationException($"任务状态 {state} 缺少 Failed 转移");
            }
        }
    }

    private static bool Reachable(string from, string target)
    {
        var visited = new HashSet<string>();
        var stack = new Stack<string>([from]);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == target)
            {
                return true;
            }

            if (!visited.Add(cur) || !Transitions.TryGetValue(cur, out var next))
            {
                continue;
            }

            foreach (var n in next)
            {
                stack.Push(n);
            }
        }

        return false;
    }
}

/// <summary>
/// SCT 子状态机（任务清单第 7 节 P6）：
/// Selecting → Retrieving → MeasuringHeight → Milling → Analyzing → UpdatingCoeff → Returning。
/// MeasuringHeight 仅测高任务经过；UpdatingCoeff 仅标准化任务经过（编排设计文档 §6）。
/// </summary>
public static class SctSubState
{
    public const string Selecting = "Selecting";
    public const string Retrieving = "Retrieving";
    public const string MeasuringHeight = "MeasuringHeight";
    public const string Milling = "Milling";
    public const string Analyzing = "Analyzing";
    public const string UpdatingCoeff = "UpdatingCoeff";
    public const string Returning = "Returning";

    /// <summary>标准化任务全链（SettingUp/TypeStd）。</summary>
    public static readonly IReadOnlyList<string> StandardizeChain =
        [Selecting, Retrieving, Milling, Analyzing, UpdatingCoeff, Returning];

    /// <summary>控制检查链（无系数更新）。</summary>
    public static readonly IReadOnlyList<string> ControlCheckChain =
        [Selecting, Retrieving, Milling, Analyzing, Returning];

    /// <summary>测高链（不激发，回填厚度）。</summary>
    public static readonly IReadOnlyList<string> HeightMeasureChain =
        [Selecting, Retrieving, MeasuringHeight, Returning];
}
