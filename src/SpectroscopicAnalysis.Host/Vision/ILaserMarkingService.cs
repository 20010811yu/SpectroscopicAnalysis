namespace SpectroscopicAnalysis.Host.Vision;

/// <summary>
/// 激光打标服务抽象：打标文本内容读写 + 打标方式切换（文本打印/条码打印，联动 EzCad2 工程文件切换）。
/// VM 依赖本接口，不直接做文件 I/O / 进程调用——MVVM 分层。
/// </summary>
public interface ILaserMarkingService
{
    /// <summary>读取打标文本内容（来源：配置路径的文本文件）。</summary>
    string ReadMarkingContent();

    /// <summary>将打标文本内容写回配置路径的文本文件。返回是否写入成功。</summary>
    bool WriteMarkingContent(string content);

    /// <summary>
    /// 切换打标方式：文本打印（2.ezd）/ 条码打印（1.ezd），联动 EzCad2 隐藏式重新加载对应工程。
    /// 返回 null 表示成功；否则为失败原因描述。
    /// </summary>
    Task<string?> SwitchMarkingModeAsync(string mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// 记录打印开始（全流程日志「激光打标」类目，格式：时间 + 打印内容 + 打印方式 + 打印开始）。
    /// 打标指令下发流程（工作流触达 EzCad2 打印）实现后调用。
    /// </summary>
    void LogPrintStarted(string content, string mode);

    /// <summary>
    /// 记录打印结束（全流程日志「激光打标」类目，格式：时间 + 打印结束 + 打印结果）；
    /// 失败时追加一条：时间 + 失败原因。
    /// </summary>
    void LogPrintFinished(bool success, string? failReason = null);

    /// <summary>
    /// 记录打印方式变更（全流程日志「激光打标」类目，格式：打印方式变更：{旧} → {新}）；
    /// 变更失败时追加一条：变更失败原因：{error}。
    /// </summary>
    void LogModeChanged(string oldMode, string newMode, bool success, string? error = null);

    /// <summary>记录打印内容变更（全流程日志「激光打标」类目，格式：打印内容变更：{旧} → {新}）。</summary>
    void LogContentChanged(string oldContent, string newContent);
}
