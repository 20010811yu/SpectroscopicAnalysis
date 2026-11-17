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
}
