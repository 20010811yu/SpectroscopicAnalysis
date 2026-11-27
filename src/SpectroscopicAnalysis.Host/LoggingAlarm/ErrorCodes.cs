namespace SpectroscopicAnalysis.Host.LoggingAlarm;

/// <summary>
/// 统一错误码常量（开发规范 11.1；新码先在 docs/开发规范.md 11.1 表登记再使用，禁止数字 magic number）。
/// </summary>
public static class ErrorCodes
{
    // 5xxx 数据与业务校验
    /// <summary>[Deprecated] 5003 样品入库校验失败（表单校验/编号或架位重复/厚度约束）。
    /// 填写类错误属用户输入问题，不再进错误链（仅 UI 提示，2026-11-20）；保留码段防复用。</summary>
    [Obsolete("填写类错误不再报警，勿再使用；保留码段防复用")]
    public const int SampleRegisterRejected = 5003;

    /// <summary>5004 样品入库写库失败（SQLite 约束/I/O）。</summary>
    public const int SampleInsertFailed = 5004;

    /// <summary>5005 样品删除失败（SQLite 约束/I/O）。</summary>
    public const int SampleDeleteFailed = 5005;

    /// <summary>5006 SCT 标样维护写库失败（测高登记/阈值调整/启用禁用）。</summary>
    public const int SctSampleMaintainFailed = 5006;

    /// <summary>5007 生产样状态回写失败（跳过生产置待分析等状态更新写库失败）。</summary>
    public const int ProductionStatusUpdateFailed = 5007;

    // 51xx SCT 策略引擎
    /// <summary>5101 控样选择无可用样品（报警暂停分析）。</summary>
    public const int SctSettingSampleUnavailable = 5101;

    /// <summary>5102 检查样超差闭环重测耗尽（报警暂停自动模式）。</summary>
    public const int SctRecheckExhausted = 5102;

    /// <summary>5103 类型标准化样无匹配（合金无对应标样）。</summary>
    public const int SctTypeStdUnavailable = 5103;

    /// <summary>5104 测高后厚度低于最小厚度（报废更换报警）。</summary>
    public const int SctThicknessBelowMinimum = 5104;

    /// <summary>5105 任务类型与样品类型不匹配（混用未勾选时 ControlCheck 须 Control 样、TypeStandardize 须 TypeStd 样）。</summary>
    public const int SctTaskSampleTypeMismatch = 5105;

    // 4xxx 通信（PLC/GNR/铣床）
    /// <summary>4005 PLC 连接配置非法（appsettings.json "Plc" 节校验失败：端口/超时/重试越界，启动快速失败）。</summary>
    public const int PlcConnectionConfigInvalid = 4005;

    // 9xxx 系统/急停
    /// <summary>9001 急停（按钮/PLC 信号/通信中断升级）。</summary>
    public const int EmergencyStop = 9001;

    /// <summary>9002 未捕获异常兜底。</summary>
    public const int UnhandledException = 9002;

    /// <summary>9003 EzCad2 自动启动失败（进程启动失败/等待超时）。</summary>
    public const int EzCad2LaunchFailed = 9003;

    /// <summary>9004 EzCad2 掉线（进程退出：Exited 事件/兜底轮询检测；主动关机与工程切换期间不报）。</summary>
    public const int EzCad2Offline = 9004;
}
