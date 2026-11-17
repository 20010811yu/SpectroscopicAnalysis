namespace SpectroscopicAnalysis.Host.Vision;

/// <summary>
/// 激光打标服务抽象：为激光打标页面提供打标文本内容（VM 依赖本接口，不直接做文件 I/O——MVVM 分层）。
/// 后续下发 EzCad2 打标指令时在此接口扩展，不改动 UI 层。
/// </summary>
public interface ILaserMarkingService
{
    /// <summary>读取打标文本内容（来源：配置路径的文本文件）。</summary>
    string ReadMarkingContent();

    /// <summary>将打标文本内容写回配置路径的文本文件。返回是否写入成功。</summary>
    bool WriteMarkingContent(string content);
}
