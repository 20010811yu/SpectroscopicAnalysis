namespace SpectroscopicAnalysis.Host.Correction;

/// <summary>单元素校正输入：原始强度与该元素的线性系数（IsCurrent 版本）。</summary>
public sealed class ElementCorrectionInput
{
    /// <summary>元素符号（如 Si/Fe）。</summary>
    public required string Element { get; init; }

    /// <summary>原始谱线强度 Ii（或强度比）。</summary>
    public required double Intensity { get; init; }

    /// <summary>斜率 ai（IsCurrent 版本；无系数时元素被跳过）。</summary>
    public required double Slope { get; init; }

    /// <summary>截距 bi。</summary>
    public required double Intercept { get; init; }

    /// <summary>单元素线性初值 Ci⁰ = ai·Ii + bi（供 IEC 迭代初值与审计展示）。</summary>
    public double LinearValue { get; set; }
}

/// <summary>干扰系数 kij：元素 j 的浓度对元素 i 结果的干扰贡献量。</summary>
public sealed class InterferenceFactor
{
    /// <summary>被干扰元素 i。</summary>
    public required string TargetElement { get; init; }

    /// <summary>干扰来源元素 j。</summary>
    public required string SourceElement { get; init; }

    /// <summary>干扰系数 kij。</summary>
    public required double Factor { get; init; }
}

/// <summary>单元素校正输出：线性初值（原始数据）与干扰校正后浓度（校正后数据，双存）。</summary>
public sealed class ElementCorrectionResult
{
    public required string Element { get; init; }

    /// <summary>线性初值 Ci⁰ = ai·Ii + bi（不含干扰项）。</summary>
    public required double LinearValue { get; init; }

    /// <summary>干扰校正后浓度 Ci（IEC 迭代收敛值；无干扰系数时 = LinearValue）。</summary>
    public required double CorrectedValue { get; init; }

    /// <summary>IEC 实际迭代轮数（无干扰系数时为 0）。</summary>
    public required int Iterations { get; init; }
}

/// <summary>整次激发的校正结果（全部元素 + 可追溯上下文）。</summary>
public sealed class CorrectionResult
{
    /// <summary>各元素校正结果（与输入元素一一对应）。</summary>
    public required IReadOnlyList<ElementCorrectionResult> Elements { get; init; }

    /// <summary>系数版本号（来源系数集合的 Version；可追溯，无系数时为 null）。</summary>
    public int? CoeffVersion { get; init; }
}

/// <summary>
/// 校正计算引擎抽象（任务清单第六节 CorrectionEngine；本层只做计算，不做设备 I/O 与落库）。
/// 模型：Ci = ai·Ii + bi + Σ_{j≠i} kij·Cj（带干扰校正，IEC 迭代 2–3 次求解）。
/// 干扰系数 kij 本期由调用方提供（厂家系数/回归工具输出）；数据表与矩阵页面随后续接入。
/// </summary>
public interface ICorrectionEngine
{
    /// <summary>
    /// 计算校正后浓度：先按线性模型求初值，再以 IEC 迭代（上限 <see cref="CorrectionEngine.MaxIecIterations"/> 轮
    /// 或相邻两轮最大相对变化 ≤ <see cref="CorrectionEngine.ConvergenceEpsilon"/>）求解干扰校正。
    /// 输入须包含 IsCurrent 线性系数；干扰系数集合可为空（此时结果即线性初值，迭代 0 轮）。
    /// </summary>
    CorrectionResult Compute(
        IReadOnlyList<ElementCorrectionInput> inputs,
        IReadOnlyList<InterferenceFactor> interferenceFactors);
}
