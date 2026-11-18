using SpectroscopicAnalysis.Host.Correction;

namespace SpectroscopicAnalysis.Tests;

/// <summary>校正计算引擎测试：线性初值 / IEC 干扰迭代收敛 / 双元素互扰 / 无干扰系数直通（任务清单第六节）。</summary>
public sealed class CorrectionEngineTests
{
    private readonly CorrectionEngine _engine = new();

    [Fact]
    public void 线性初值_按斜率截距计算()
    {
        var inputs = new List<ElementCorrectionInput>
        {
            new() { Element = "Si", Intensity = 100, Slope = 0.5, Intercept = 0.2 },
        };

        var result = _engine.Compute(inputs, Array.Empty<InterferenceFactor>());

        var si = result.Elements.Single();
        Assert.Equal(50.2, si.LinearValue, 6);
        Assert.Equal(50.2, si.CorrectedValue, 6);       // 无干扰系数：校正后 = 线性初值
        Assert.Equal(0, si.Iterations);
    }

    [Fact]
    public void 双元素互扰_IEC迭代收敛到稳定解()
    {
        // Si 对 Fe 干扰 0.02、Fe 对 Si 干扰 0.05：构造互相耦合方程，验证迭代收敛
        var inputs = new List<ElementCorrectionInput>
        {
            new() { Element = "Si", Intensity = 100, Slope = 1.0, Intercept = 0.0 },    // 初值 100
            new() { Element = "Fe", Intensity = 50, Slope = 1.0, Intercept = 0.0 },     // 初值 50
        };
        var factors = new List<InterferenceFactor>
        {
            new() { TargetElement = "Fe", SourceElement = "Si", Factor = 0.02 },        // Fe += 0.02·Si
            new() { TargetElement = "Si", SourceElement = "Fe", Factor = 0.05 },        // Si += 0.05·Fe
        };

        var result = _engine.Compute(inputs, factors);

        var si = result.Elements.Single(e => e.Element == "Si");
        var fe = result.Elements.Single(e => e.Element == "Fe");

        // 手工推演 2 轮迭代后的解析值（第 3 轮相对变化 < 1e-2，4 轮内应高度接近）：
        // Si1 = 100 + 0.05·50 = 102.5, Fe1 = 50 + 0.02·100 = 52
        // Si2 = 100 + 0.05·52 = 102.6, Fe2 = 50 + 0.02·102.5 = 52.05
        Assert.Equal(102.6, si.CorrectedValue, 1);
        Assert.Equal(52.05, fe.CorrectedValue, 2);
        Assert.InRange(si.Iterations, 2, CorrectionEngine.MaxIecIterations);
        Assert.Equal(si.Iterations, fe.Iterations);
    }

    [Fact]
    public void 干扰元素缺失系数时按零计()
    {
        // Fe 受 Cu 干扰，但输入只有 Fe（无 Cu）→ 干扰项按 0，结果 = 线性初值
        var inputs = new List<ElementCorrectionInput>
        {
            new() { Element = "Fe", Intensity = 50, Slope = 1.0, Intercept = 1.0 },
        };
        var factors = new List<InterferenceFactor>
        {
            new() { TargetElement = "Fe", SourceElement = "Cu", Factor = 0.1 },
        };

        var result = _engine.Compute(inputs, factors);

        var fe = result.Elements.Single();
        Assert.Equal(51.0, fe.LinearValue, 6);
        Assert.Equal(51.0, fe.CorrectedValue, 6);
    }

    [Fact]
    public void 弱干扰_相对变化达标后提前收敛()
    {
        // 干扰系数 1e-9：一轮后相对变化已小于 Epsilon，不应跑满 3 轮
        var inputs = new List<ElementCorrectionInput>
        {
            new() { Element = "A", Intensity = 10, Slope = 1.0, Intercept = 0.0 },
            new() { Element = "B", Intensity = 10, Slope = 1.0, Intercept = 0.0 },
        };
        var factors = new List<InterferenceFactor>
        {
            new() { TargetElement = "A", SourceElement = "B", Factor = 1e-9 },
        };

        var result = _engine.Compute(inputs, factors);

        var a = result.Elements.Single(e => e.Element == "A");
        Assert.Equal(1, a.Iterations);                      // 首轮即收敛
        Assert.Equal(10.0 + 1e-8, a.CorrectedValue, 6);
    }

    [Fact]
    public void 线性初值回写输入_供原始数据留痕()
    {
        var input = new ElementCorrectionInput { Element = "Zn", Intensity = 8, Slope = 2.0, Intercept = 0.5 };

        _engine.Compute(new[] { input }, Array.Empty<InterferenceFactor>());

        Assert.Equal(16.5, input.LinearValue, 6);           // LinearValue 回写到输入对象（审计/双存用）
    }
}
