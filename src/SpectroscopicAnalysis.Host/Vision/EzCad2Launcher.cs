using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpectroscopicAnalysis.Host.LoggingAlarm;

namespace SpectroscopicAnalysis.Host.Vision;

/// <summary>
/// EzCad2 激光打标软件启动配置（appsettings.json「EzCad2」节）。
/// </summary>
public class EzCad2Options
{
    /// <summary>可执行文件完整路径；为空时回退到当前用户桌面 EzCad2.exe。</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>启动时自动加载的打标工程文件（.ezd）完整路径；为空时仅启动软件不加载文件。</summary>
    public string EzdFilePath { get; set; } = string.Empty;

    /// <summary>启动后等待主窗口就绪的超时（毫秒，集中配置原则）。</summary>
    public int WaitForWindowTimeoutMs { get; set; } = 15000;
}

/// <summary>
/// 随上位机启动自动拉起 EzCad2.exe（激光打标软件），并以命令行参数加载配置的 .ezd 工程文件：
/// 启动后强制最小化并完全隐藏窗口，使其不出现在桌面与任务栏。
/// 失败仅记录错误（9003），不阻断上位机启动（打标软件为辅助外部程序）。
/// 上位机退出时联动关闭 EzCad2：先尝试优雅关闭（WM_CLOSE），超时后强制结束进程。
/// </summary>
public class EzCad2Launcher : BackgroundService
{
    private readonly EzCad2Options _options;
    private readonly ILogger<EzCad2Launcher> _logger;
    private readonly IErrorCollector _errorCollector;

    /// <summary>本服务拉起的 EzCad2 进程；退出时联动关闭（不 Dispose，进程对象由服务持有至 StopAsync）。</summary>
    private Process? _launchedProcess;

    /// <summary>启动时记录的主窗口句柄；隐藏窗口后 Process.MainWindowHandle 会返回 0，关闭时须用本句柄。</summary>
    private IntPtr _mainWindowHandle;

    public EzCad2Launcher(IConfiguration configuration, ILogger<EzCad2Launcher> logger, IErrorCollector errorCollector)
    {
        _options = configuration.GetSection("EzCad2").Get<EzCad2Options>() ?? new EzCad2Options();
        _logger = logger;
        _errorCollector = errorCollector;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Run(() => Launch(stoppingToken), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 上位机退出期间被取消：属正常关闭路径，不按错误处理
        }
        catch (Exception ex)
        {
            // 携带登记错误码走统一错误链（9003），不吞异常
            _errorCollector.Collect(ErrorCodes.EzCad2LaunchFailed, "EzCad2 自动启动失败", exception: ex);
        }
    }

    /// <summary>
    /// 上位机退出时联动关闭 EzCad2：先优雅关闭（WM_CLOSE，等待进程自行清理退出），
    /// 超时后强制 Kill 兜底。失败仅记录，不影响上位机退出流程。
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var process = _launchedProcess;
        if (process != null && !process.HasExited)
        {
            try
            {
                process.Refresh();
                // 优雅关闭：向启动时记录的主窗口句柄投递 WM_CLOSE（隐藏后 Process.MainWindowHandle 返回 0，不可用）
                var mainWindow = _mainWindowHandle != IntPtr.Zero && IsWindow(_mainWindowHandle)
                    ? _mainWindowHandle
                    : process.MainWindowHandle;
                if (mainWindow != IntPtr.Zero)
                {
                    PostMessage(mainWindow, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }

                // 等待宽限期，未退出则强制结束（隐藏窗口下若弹出保存确认框无法交互，走此兜底）
                if (!process.WaitForExit(ShutdownGraceMs) && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    _logger.LogWarning("EzCad2 优雅关闭超时，已强制结束进程");
                }

                _logger.LogInformation("EzCad2 已随上位机关闭");
                process.Dispose();
            }
            catch (Exception ex)
            {
                // 关闭失败不阻断上位机退出；记录 9003 供排查
                _errorCollector.Collect(ErrorCodes.EzCad2LaunchFailed, "EzCad2 联动关闭失败", exception: ex);
            }
        }

        await base.StopAsync(cancellationToken);
    }

    private void Launch(CancellationToken stoppingToken)
    {
        var exePath = ResolveExePath();
        if (string.IsNullOrEmpty(exePath))
        {
            _logger.LogWarning("EzCad2.exe 未找到（配置路径与桌面均不存在），跳过自动启动");
            return;
        }

        // 启动参数：可配置的 .ezd 工程文件（EzCad2 接收文件路径作为命令行参数直接打开）
        var arguments = ResolveEzdArgument();
        // 已有 EzCad2 在后台运行时先关闭（确保以最新配置/工程文件重新拉起），再重新启动
        ShutdownExistingProcesses();

        _logger.LogInformation("自动启动 EzCad2：{ExePath} {Arguments}", exePath, arguments);

        // 不用 using：进程对象须存活到 StopAsync 联动关闭，Dispose 后再访问其成员会抛 ObjectDisposedException
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,
                // 窗口最小化启动（多数 Win32 程序响应 STARTUPINFO 的 SW_MINIMIZE）
                WindowStyle = ProcessWindowStyle.Minimized,
            },
            EnableRaisingEvents = true,
        };
        if (!process.Start())
        {
            _errorCollector.Collect(ErrorCodes.EzCad2LaunchFailed, "EzCad2 进程启动返回失败");
            return;
        }

        // 留存进程引用，供 StopAsync 在上位机退出时联动关闭（此处不 Dispose，避免句柄提前失效）
        _launchedProcess = process;

        // 等主窗口句柄出现（消息循环就绪后才能改样式；集中配置超时，防止无限等待）
        var mainWindow = WaitForMainWindow(process, _options.WaitForWindowTimeoutMs, stoppingToken);
        if (mainWindow == IntPtr.Zero)
        {
            _logger.LogWarning("EzCad2 未探测到主窗口句柄，跳过任务栏隐藏");
            return;
        }

        // 强制最小化（程序可能无视启动参数自行弹出窗口）→ 隐藏窗口 → 加工具窗样式确保任务栏无图标
        _mainWindowHandle = mainWindow;
        ShowWindow(mainWindow, SW_MINIMIZE);
        ApplyToolWindowStyle(mainWindow);
        ShowWindow(mainWindow, SW_HIDE);
        _logger.LogInformation("EzCad2 已启动，窗口已隐藏且不在任务栏显示");
    }

    /// <summary>
    /// 配置路径优先，为空或不存在时回退到当前用户桌面；
    /// 桌面上常见的是快捷方式（.lnk），需解析其目标 exe 实际路径。
    /// </summary>
    private string? ResolveExePath()
    {
        if (!string.IsNullOrWhiteSpace(_options.ExecutablePath) && File.Exists(_options.ExecutablePath))
        {
            return _options.ExecutablePath;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var exe = Path.Combine(desktop, "EzCad2.exe");
        if (File.Exists(exe))
        {
            return exe;
        }

        var shortcut = Path.Combine(desktop, "EzCad2.exe.lnk");
        return File.Exists(shortcut) ? ResolveShortcutTarget(shortcut) : null;
    }

    /// <summary>解析 .lnk 快捷方式的目标 exe 路径（WScript.Shell COM；解析失败返回 null）。</summary>
    private string? ResolveShortcutTarget(string shortcutPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                var target = (string)shell.CreateShortcut(shortcutPath).TargetPath;
                return File.Exists(target) ? target : null;
            }
            finally
            {
                Marshal.ReleaseComObject(shell);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析 EzCad2 快捷方式失败：{ShortcutPath}", shortcutPath);
            return null;
        }
    }

    /// <summary>
    /// 构造启动参数：配置的 .ezd 文件存在时作为参数传入（含引号防路径空格）；
    /// 文件不存在仅记录警告，仍正常启动软件（不阻断打标软件可用性）。
    /// </summary>
    private string ResolveEzdArgument()
    {
        if (string.IsNullOrWhiteSpace(_options.EzdFilePath))
        {
            return string.Empty;
        }

        if (!File.Exists(_options.EzdFilePath))
        {
            _logger.LogWarning("配置的 Ezd 工程文件不存在，启动时不加载：{EzdPath}", _options.EzdFilePath);
            return string.Empty;
        }

        return $"\"{_options.EzdFilePath}\"";
    }

    /// <summary>
    /// 检查并关闭已在后台运行的 EzCad2 进程（按 exe 文件名匹配，不限于本程序拉起的实例）：
    /// 先投递 WM_CLOSE 优雅关闭并等待宽限期，未退出则强制结束。失败仅记录，继续重新启动流程。
    /// </summary>
    private void ShutdownExistingProcesses()
    {
        var processName = Path.GetFileNameWithoutExtension("EzCad2.exe");
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                using (process)
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    var mainWindow = process.MainWindowHandle != IntPtr.Zero ? process.MainWindowHandle : IntPtr.Zero;
                    if (mainWindow != IntPtr.Zero)
                    {
                        PostMessage(mainWindow, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }

                    if (!process.WaitForExit(ShutdownGraceMs) && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        _logger.LogWarning("EzCad2 优雅关闭超时，已强制结束残留进程（PID {Pid}）", process.Id);
                    }
                    else
                    {
                        _logger.LogInformation("已关闭后台残留的 EzCad2 进程（PID {Pid}）", process.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                // 单个残留进程关闭失败不阻断后续重新启动；记录 9003 供排查
                _errorCollector.Collect(ErrorCodes.EzCad2LaunchFailed, "关闭后台残留 EzCad2 进程失败", exception: ex);
            }
        }
    }

    /// <summary>追加 WS_EX_TOOLWINDOW 扩展样式：窗口不出现在任务栏（不改变最小化状态与可见性）。</summary>
    private static void ApplyToolWindowStyle(IntPtr hWnd)
    {
        var exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        SetWindowLong(hWnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
    }

    // Win32 扩展样式操作（仅外部窗口样式设置，不涉及业务）
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const int SW_MINIMIZE = 6;
    private const int SW_HIDE = 0;

    /// <summary>优雅关闭宽限期（毫秒）：超时未退出则强制结束进程（上限受 Host 停止超时 5s 约束）。</summary>
    private const int ShutdownGraceMs = 3000;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern long GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern long SetWindowLong(IntPtr hWnd, int nIndex, long dwNewLong);

    /// <summary>启动等待期间句柄探测重试（MainWindowHandle 需要窗口消息循环就绪，属启动期瞬态，非通信重试）。</summary>
    private static IntPtr WaitForMainWindow(Process process, int timeoutMs, CancellationToken stoppingToken)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !stoppingToken.IsCancellationRequested)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }

            Thread.Sleep(200);
        }

        return IntPtr.Zero;
    }
}
