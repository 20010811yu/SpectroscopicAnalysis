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

    /// <summary>启动后等待主窗口就绪的超时（毫秒，集中配置原则）。</summary>
    public int WaitForWindowTimeoutMs { get; set; } = 15000;
}

/// <summary>
/// 随上位机启动自动拉起 EzCad2.exe（激光打标软件）：
/// 窗口最小化启动，并通过 WS_EX_TOOLWINDOW 扩展样式使其不出现在任务栏。
/// 失败仅记录错误（9003），不阻断上位机启动（打标软件为辅助外部程序）。
/// </summary>
public class EzCad2Launcher : BackgroundService
{
    private readonly EzCad2Options _options;
    private readonly ILogger<EzCad2Launcher> _logger;
    private readonly IErrorCollector _errorCollector;

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

    private void Launch(CancellationToken stoppingToken)
    {
        var exePath = ResolveExePath();
        if (string.IsNullOrEmpty(exePath))
        {
            _logger.LogWarning("EzCad2.exe 未找到（配置路径与桌面均不存在），跳过自动启动");
            return;
        }

        _logger.LogInformation("自动启动 EzCad2：{ExePath}", exePath);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
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

        // 等主窗口句柄出现（消息循环就绪后才能改样式；集中配置超时，防止无限等待）
        var mainWindow = WaitForMainWindow(process, _options.WaitForWindowTimeoutMs, stoppingToken);
        if (mainWindow == IntPtr.Zero)
        {
            _logger.LogWarning("EzCad2 未探测到主窗口句柄，跳过任务栏隐藏");
            return;
        }

        // 强制最小化（程序可能无视启动参数自行弹出窗口）→ 隐藏窗口 → 加工具窗样式确保任务栏无图标
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

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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
