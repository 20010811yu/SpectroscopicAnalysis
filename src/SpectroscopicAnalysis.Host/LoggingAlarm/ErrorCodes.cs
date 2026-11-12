namespace SpectroscopicAnalysis.Host.LoggingAlarm;

/// <summary>
/// 统一错误码常量（开发规范 11.1；新码先在 docs/开发规范.md 11.1 表登记再使用，禁止数字 magic number）。
/// </summary>
public static class ErrorCodes
{
    // 5xxx 数据与业务校验
    /// <summary>5003 样品入库校验失败（表单校验/编号或架位重复/厚度约束）。</summary>
    public const int SampleRegisterRejected = 5003;

    /// <summary>5004 样品入库写库失败（SQLite 约束/I/O）。</summary>
    public const int SampleInsertFailed = 5004;

    /// <summary>5005 样品删除失败（SQLite 约束/I/O）。</summary>
    public const int SampleDeleteFailed = 5005;

    /// <summary>5006 SCT 标样维护写库失败（测高登记/阈值调整/启用禁用）。</summary>
    public const int SctSampleMaintainFailed = 5006;

    // 51xx SCT 策略引擎
    /// <summary>5101 设置样选择无可用样品（报警暂停分析）。</summary>
    public const int SctSettingSampleUnavailable = 5101;

    /// <summary>5102 控制样超差闭环重测耗尽（报警暂停自动模式）。</summary>
    public const int SctRecheckExhausted = 5102;

    /// <summary>5103 类型标准化样无匹配（合金无对应标样）。</summary>
    public const int SctTypeStdUnavailable = 5103;

    /// <summary>5104 测高后厚度低于最小厚度（报废更换报警）。</summary>
    public const int SctThicknessBelowMinimum = 5104;

    // 9xxx 系统/急停
    /// <summary>9002 未捕获异常兜底。</summary>
    public const int UnhandledException = 9002;
}
