using SpectroscopicAnalysis.Host.Data.Models;

namespace SpectroscopicAnalysis.Host.SampleManager;

/// <summary>
/// 货架目录：静态布局常量 + 可用架位查询（注册下拉选择用）。
/// 布局与控制台货架示意一致（5 行 × 5 列，架位码 A01–E05）；
/// 架位无独立表，占用即样品库中 RackPosition 的出现。
/// </summary>
public static class RackDirectory
{
    /// <summary>货架行数（行标签 A–E）。</summary>
    public const int Rows = 5;

    /// <summary>每列架位数。</summary>
    public const int Columns = 5;

    /// <summary>行标签（与控制台货架示意一致）。</summary>
    public static readonly string[] RowLabels = { "A", "B", "C", "D", "E" };

    /// <summary>全部架位码（A01–E05，行优先排序）。</summary>
    public static IReadOnlyList<string> AllPositions { get; } =
        RowLabels.SelectMany(r => Enumerable.Range(1, Columns).Select(c => $"{r}{c:00}")).ToList();

    /// <summary>
    /// 查询当前可用（空闲）架位：全布局减去标样库与生产样已占用的 RackPosition。
    /// 超出示意范围的既有架位码（如 A12）视为占用，不参与生成。
    /// </summary>
    public static IReadOnlyList<string> AvailablePositions(
        IEnumerable<SctSample> sctSamples, IEnumerable<ProductionSample> productionSamples)
    {
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sctSamples)
        {
            if (!string.IsNullOrEmpty(s.RackPosition))
            {
                occupied.Add(s.RackPosition.Trim());
            }
        }
        foreach (var p in productionSamples)
        {
            if (!string.IsNullOrEmpty(p.RackPosition))
            {
                occupied.Add(p.RackPosition.Trim());
            }
        }

        return AllPositions.Where(code => !occupied.Contains(code)).ToList();
    }

    /// <summary>生产样货架（分析样货架）行数（独立编号空间，与 SCT 货架 A01–E05 互不冲突）。</summary>
    public const int ProductionRows = 5;

    /// <summary>生产样货架每列架位数。</summary>
    public const int ProductionColumns = 5;

    /// <summary>生产样全部架位码（P01–P25，行优先排序）。</summary>
    public static IReadOnlyList<string> ProductionAllPositions { get; } =
        Enumerable.Range(1, ProductionRows * ProductionColumns).Select(i => $"P{i:00}").ToList();

    /// <summary>
    /// 查询生产样当前可用（空闲）架位：P01–P25 减去生产样已占用的 RackPosition。
    /// 生产样与 SCT 样架位编号空间独立，互不占用。
    /// </summary>
    public static IReadOnlyList<string> ProductionAvailablePositions(
        IEnumerable<ProductionSample> productionSamples)
    {
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in productionSamples)
        {
            if (!string.IsNullOrEmpty(p.RackPosition))
            {
                occupied.Add(p.RackPosition.Trim());
            }
        }

        return ProductionAllPositions.Where(code => !occupied.Contains(code)).ToList();
    }
}
