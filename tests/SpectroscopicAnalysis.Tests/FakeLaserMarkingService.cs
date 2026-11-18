using SpectroscopicAnalysis.Host.Vision;

namespace SpectroscopicAnalysis.Tests;

/// <summary>Fake 激光打标服务：内存读写打标文本，不做文件 I/O / 进程调用。</summary>
internal sealed class FakeLaserMarkingService : ILaserMarkingService
{
    public string Content { get; set; } = string.Empty;

    public string? SwitchError { get; set; }

    public string ReadMarkingContent() => Content;

    public bool WriteMarkingContent(string content)
    {
        Content = content;
        return true;
    }

    public Task<string?> SwitchMarkingModeAsync(string mode, CancellationToken cancellationToken = default) =>
        Task.FromResult(SwitchError);
}
