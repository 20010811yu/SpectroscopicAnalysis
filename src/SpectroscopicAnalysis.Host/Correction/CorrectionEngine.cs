namespace SpectroscopicAnalysis.Host.Correction;

/// <summary>
/// 校正计算引擎实现（纯计算，无 I/O；任务清单第六节）：
/// 1) 线性初值 Ci⁰ = ai·Ii + bi；
/// 2) IEC 干扰校正迭代 Ci^(k+1) = ai·Ii + bi + Σ_{j≠i} kij·Cj^(k)，
///    收敛条件：相邻两轮最大相对变化 ≤ Epsilon，或达到 MaxIecIterations 轮上限（任务清单：迭代 2–3 次）。
/// 校正后浓度与线性初值双存（原始/校正后），系数版本随结果返回（可追溯）。
/// </summary>
public sealed class CorrectionEngine : ICorrectionEngine
{
    /// <summary>IEC 迭代轮数上限（任务清单第六节：迭代 2–3 次求解）。</summary>
    public const int MaxIecIterations = 3;

    /// <summary>收敛阈值（相邻两轮最大相对变化；防弱干扰空转满 3 轮）。</summary>
    public const double ConvergenceEpsilon = 1e-6;

    public CorrectionResult Compute(
        IReadOnlyList<ElementCorrectionInput> inputs,
        IReadOnlyList<InterferenceFactor> interferenceFactors)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(interferenceFactors);

        // 线性初值（原始数据；干扰项的迭代起点）
        foreach (var input in inputs)
        {
            input.LinearValue = input.Slope * input.Intensity + input.Intercept;
        }

        // 按目标元素索引干扰系数：kij 表[i] = (来源元素 j → kij) 列表
        var kByTarget = interferenceFactors
            .GroupBy(k => k.TargetElement)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 当前迭代值（第 0 轮 = 线性初值）
        var current = inputs.ToDictionary(x => x.Element, x => x.LinearValue);
        var iterations = 0;

        // 有干扰系数才需要迭代；无系数直接输出线性初值（迭代 0 轮）
        if (interferenceFactors.Count > 0)
        {
            for (var iter = 1; iter <= MaxIecIterations; iter++)
            {
                iterations = iter;
                var maxRelativeChange = 0.0;
                var next = new Dictionary<string, double>(inputs.Count);

                foreach (var input in inputs)
                {
                    // 干扰项 Σ_{j≠i} kij·Cj（j 为当前迭代值；元素无系数时按 0 计）
                    var interference = 0.0;
                    if (kByTarget.TryGetValue(input.Element, out var factors))
                    {
                        foreach (var k in factors)
                        {
                            interference += k.Factor * current.GetValueOrDefault(k.SourceElement);
                        }
                    }

                    next[input.Element] = input.Slope * input.Intensity + input.Intercept + interference;
                }

                // 收敛判定：本轮相对上一轮的最大相对变化
                foreach (var input in inputs)
                {
                    var prev = current[input.Element];
                    var updated = next[input.Element];
                    var relative = prev == 0
                        ? Math.Abs(updated)
                        : Math.Abs(updated - prev) / Math.Abs(prev);
                    if (relative > maxRelativeChange)
                    {
                        maxRelativeChange = relative;
                    }
                }

                current = next;
                if (maxRelativeChange <= ConvergenceEpsilon)
                {
                    break;
                }
            }
        }

        return new CorrectionResult
        {
            Elements = inputs.Select(x => new ElementCorrectionResult
            {
                Element = x.Element,
                LinearValue = x.LinearValue,
                CorrectedValue = current.GetValueOrDefault(x.Element),
                Iterations = iterations,
            }).ToList(),
            CoeffVersion = null, // 版本号由调用方按系数集合补充（引擎不感知存储）
        };
    }
}
