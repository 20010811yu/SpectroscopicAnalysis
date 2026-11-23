using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpectroscopicAnalysis.Host.LoggingAlarm;

namespace SpectroscopicAnalysis.Host.Vision;

/// <summary>
/// EzCad2 控制抽象：运行时切换打标工程文件（关闭当前实例并以指定 .ezd 重新隐藏式拉起）。
/// UI 层经本接口操作（MVVM 分层，不直接依赖 BackgroundService 实现）。
/// </summary>
public interface IEzCad2Controller
{
    /// <summary>切换打标工程文件。返回 null 表示成功；否则为失败原因描述。</summary>
    Task<string?> SwitchEzdFileAsync(string ezdPath, CancellationToken cancellationToken = default);

    /// <summary>EzCad2 是否在线（进程存活 = 在线；未启动或已退出 = 掉线）。设备常驻栏状态源。</summary>
    bool IsOnline { get; }

    /// <summary>在线状态变化事件（任意线程触发，订阅方自行切回 UI 线程）。</summary>
    event Action? OnlineStateChanged;
}

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

    /// <summary>是否隐藏 EzCad2 窗口（false = 正常显示窗口；调试期暂不隐藏）。</summary>
    public bool HideWindow { get; set; } = true;

    /// <summary>在线状态兜底轮询周期（毫秒，集中配置原则）；进程 Exited 事件之外的掉线检测兜底。</summary>
    public int OnlinePollIntervalMs { get; set; } = 2000;
}

/// <summary>
/// 随上位机启动自动拉起 EzCad2.exe（激光打标软件），并以命令行参数加载配置的 .ezd 工程文件：
/// 启动后强制最小化并完全隐藏窗口，使其不出现在桌面与任务栏。
/// 失败仅记录错误（9003），不阻断上位机启动（打标软件为辅助外部程序）。
/// 上位机退出时联动关闭 EzCad2：先尝试优雅关闭（WM_CLOSE），超时后强制结束进程。
/// </summary>
public class EzCad2Launcher : BackgroundService, IEzCad2Controller
{
    private readonly EzCad2Options _options;
    private readonly ILogger<EzCad2Launcher> _logger;
    private readonly IErrorCollector _errorCollector;

    /// <summary>宿主应用停止令牌：ExecuteAsync 的 stoppingToken 在部分宿主版本关机时不会取消，
    /// 守护循环必须额外监听 ApplicationStopping 才能在上位机退出时及时结束（否则 base.StopAsync 永久等待导致卡死）。</summary>
    private readonly CancellationToken _appStopping;

    /// <summary>启动/切换/联动关闭互斥锁：避免并发重启 EzCad2 造成进程与句柄状态混乱。</summary>
    private readonly SemaphoreSlim _launchLock = new(1, 1);

    /// <summary>本服务拉起的 EzCad2 进程；退出时联动关闭（不 Dispose，进程对象由服务持有至 StopAsync）。</summary>
    private Process? _launchedProcess;

    /// <summary>启动时记录的主窗口句柄；隐藏窗口后 Process.MainWindowHandle 会返回 0，关闭时须用本句柄。</summary>
    private IntPtr _mainWindowHandle;

    /// <summary>EzCad2 在线状态（进程存活 = 在线）；volatile 保证守护/退出线程与 UI 读可见。</summary>
    private volatile bool _isOnline;

    /// <inheritdoc />
    public bool IsOnline => _isOnline;

    /// <inheritdoc />
    public event Action? OnlineStateChanged;

    /// <summary>更新在线状态并在变化时触发事件（可从进程 Exited 回调线程调用）。
    /// 掉线时进统一错误链报警（9004）；主动关机与工程切换期间不报（见 _shuttingDown/_switching）。</summary>
    private void SetOnline(bool online)
    {
        if (_isOnline == online)
        {
            return;
        }

        _isOnline = online;
        if (!online && !_shuttingDown && !_switching)
        {
            // 掉线一次性报警（状态变化才进本方法，天然不刷屏）；恢复在线仅翻状态不报警
            _errorCollector.Collect(ErrorCodes.EzCad2Offline, "激光打标机 EzCad2 已离线（进程退出）");
        }

        OnlineStateChanged?.Invoke();
    }

    /// <summary>上位机主动关机标志：联动关闭 EzCad2 导致的进程退出不算掉线故障。</summary>
    private volatile bool _shuttingDown;

    /// <summary>工程切换中标志：切换期间旧实例被杀/新实例拉起的短暂离线不进报警链（状态仍如实翻转）。</summary>
    private volatile bool _switching;

    /// <summary>
    /// 在线状态兜底轮询守护循环（独立于隐藏守护，随宿主常驻）：
    /// 进程 Exited 事件可能丢失（外部 taskkill/进程被外部拉起等场景），低频按进程名核验存活兜底。
    /// 阻塞调用线程（Task.Run 线程）直至上位机退出。
    /// </summary>
    private void OnlineGuardLoop(CancellationToken stoppingToken)
    {
        var interval = Math.Max(500, _options.OnlinePollIntervalMs);
        while (!stoppingToken.IsCancellationRequested)
        {
            var alive = false;
            try
            {
                var own = _launchedProcess;
                if (own is not null)
                {
                    try
                    {
                        alive = !own.HasExited;
                    }
                    catch (InvalidOperationException)
                    {
                        // 进程对象未关联（启动流程被打断等）：交由按进程名兜底判定
                    }
                }

                if (!alive && Process.GetProcessesByName("EzCad2").Length > 0)
                {
                    alive = true;   // 非本服务拉起的 EzCad2 实例存活同样视为在线
                }
            }
            catch (Exception ex)
            {
                // 进程探测失败不改变现有状态（保守：不误报掉线），仅记录排查
                _logger.LogDebug(ex, "EzCad2 在线兜底轮询探测失败");
            }

            SetOnline(alive);
            Thread.Sleep(interval);
        }
    }

    public EzCad2Launcher(IConfiguration configuration, IHostApplicationLifetime appLifetime, ILogger<EzCad2Launcher> logger, IErrorCollector errorCollector)
    {
        _options = configuration.GetSection("EzCad2").Get<EzCad2Options>() ?? new EzCad2Options();
        _appStopping = appLifetime.ApplicationStopping;
        _hideWindow = _options.HideWindow;
        _logger = logger;
        _errorCollector = errorCollector;
    }

    /// <summary>是否隐藏窗口的运行时快照（构造时取自配置，回调与收尾逻辑共用）。</summary>
    private readonly bool _hideWindow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 注意：Launch 与守护循环必须整体在 Task.Run 的后台线程执行——
            // WPF 同步上下文会把 await 之后的延续调度回 UI 线程，若守护循环在 UI 线程同步阻塞将冻结整个界面
            await Task.Run(() =>
            {
                Launch(stoppingToken, _options.EzdFilePath);

                // 常驻隐藏守护：EzCad2 会自行重新显示最小化形态的迷你条窗（屏幕底部约 160x28），反复重绘造成闪烁；
                // 上位机运行期间持续轮询，发现可见主窗口立即隐藏（150ms 轮询，开销可忽略）
                if (_hideWindow)
                {
                    HideGuardLoop(_appStopping);
                }
            }, _appStopping);

            // 在线状态兜底轮询：独立后台线程（不依赖 HideWindow 开关；Exited 事件之外的掉线检测兜底）
            _ = Task.Run(() => OnlineGuardLoop(_appStopping), _appStopping);
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
    /// 常驻隐藏守护循环：轮询期间发现主窗口可见（EzCad2 自行重新显示迷你条窗等）立即最小化并隐藏。
    /// 阻塞调用线程（Task.Run 线程）直至上位机退出。
    /// </summary>
    private void HideGuardLoop(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_mainWindowHandle != IntPtr.Zero && IsWindow(_mainWindowHandle) && IsWindowVisible(_mainWindowHandle))
            {
                ShowWindow(_mainWindowHandle, SW_MINIMIZE);
                ApplyToolWindowStyle(_mainWindowHandle);
                ShowWindow(_mainWindowHandle, SW_HIDE);
                _logger.LogDebug("守护循环重新隐藏了 EzCad2 自行显示的窗口");
            }

            Thread.Sleep(150);
        }
    }

    /// <summary>
    /// 上位机退出时联动关闭 EzCad2：先优雅关闭（WM_CLOSE）并异步有界等待，超时强制 Kill；
    /// 最后按进程名兜底清理所有 EzCad2 残留（覆盖切换期间产生的孤儿实例）。
    /// 全程异步等待，不阻塞 UI 线程；失败仅记录，不影响上位机退出流程。
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _shuttingDown = true;   // 联动关闭导致的进程退出不按掉线故障报警
            var process = _launchedProcess;
            var associated = false;
            if (process != null)
            {
                // 进程对象可能未关联句柄（启动流程被并发实例打断等），视为无可关闭对象直接跳过
                try
                {
                    associated = !process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    _logger.LogDebug("EzCad2 进程对象未关联（无可关闭对象），跳过联动关闭");
                }
            }

            if (associated && process != null)
            {
                // 直接强制结束：WM_CLOSE 优雅关闭会让 EzCad2 在退出途中弹出闪屏（隐藏守护此时已停止），
                // 强杀则窗口根本不会出现。EzCad2 自带 AUTOSAVE 机制，强杀无数据丢失风险。
                process.Kill(entireProcessTree: true);
                _logger.LogInformation("EzCad2 已随上位机关闭");
            }

            // 兜底：按进程名清理所有 EzCad2 残留（覆盖工程切换期间的孤儿实例）
            foreach (var leftover in Process.GetProcessesByName("EzCad2"))
            {
                try
                {
                    if (!leftover.HasExited)
                    {
                        leftover.Kill(entireProcessTree: true);
                        _logger.LogWarning("已强制结束残留 EzCad2 进程（PID {Pid}）", leftover.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "清理残留 EzCad2 进程失败（PID {Pid}）", leftover.Id);
                }
                finally
                {
                    leftover.Dispose();
                }
            }

            process?.Dispose();
        }
        catch (Exception ex)
        {
            // 关闭失败不阻断上位机退出；记录 9003 供排查
            _errorCollector.Collect(ErrorCodes.EzCad2LaunchFailed, "EzCad2 联动关闭失败", exception: ex);
        }

        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// 运行时切换打标工程文件：关闭当前 EzCad2 实例并以指定 .ezd 重新隐藏式拉起（含选中/守护等既有流程）。
    /// 供打标方式下拉框切换调用（文本打印 2.ezd / 条码打印 1.ezd）。
    /// </summary>
    public async Task<string?> SwitchEzdFileAsync(string ezdPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ezdPath) || !File.Exists(ezdPath))
        {
            return $"工程文件不存在：{ezdPath}";
        }

        await _launchLock.WaitAsync(cancellationToken);
        try
        {
            // 切换期间旧实例被杀/新实例拉起会产生短暂"离线"，抑制报警（状态仍如实翻转）
            _switching = true;
            await Task.Run(() => Launch(cancellationToken, ezdPath), cancellationToken);
            _logger.LogInformation("已切换 EzCad2 工程文件：{EzdPath}", ezdPath);
            return null;
        }
        catch (Exception ex)
        {
            _errorCollector.Collect(ErrorCodes.EzCad2LaunchFailed, $"切换 EzCad2 工程文件失败：{ezdPath}", exception: ex);
            return $"切换失败：{ex.Message}";
        }
        finally
        {
            _switching = false;
            _launchLock.Release();
        }
    }

    private void Launch(CancellationToken stoppingToken, string ezdFilePath)
    {
        var exePath = ResolveExePath();
        if (string.IsNullOrEmpty(exePath))
        {
            _logger.LogWarning("EzCad2.exe 未找到（配置路径与桌面均不存在），跳过自动启动");
            return;
        }

        // 启动参数：指定的 .ezd 工程文件（EzCad2 接收文件路径作为命令行参数直接打开）
        var arguments = ResolveEzdArgument(ezdFilePath);
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
                // 隐藏模式下窗口最小化启动；调试显示模式正常启动
                WindowStyle = _hideWindow ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal,
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

        // 在线状态：进程启动成功即在线；Exited 事件（EnableRaisingEvents 已开）置掉线并通知设备常驻栏
        SetOnline(true);
        process.Exited += (_, _) => SetOnline(false);

        // 事件驱动隐藏：WinEvent 钩子监听该进程所有窗口的 SHOW 事件，窗口一显示立即隐藏（含启动闪屏）；
        // 钩子回调依赖消息泵，在专用线程上安装并泵消息，等待期结束后投递 WM_QUIT 收尾
        _mainWindowHandle = IntPtr.Zero;
        StartShowEventWatcher(process.Id, _options.WaitForWindowTimeoutMs, stoppingToken);

        _logger.LogInformation("EzCad2 已启动，窗口已隐藏且不在任务栏显示");
    }

    /// <summary>
    /// 在专用线程上安装 EVENT_OBJECT_SHOW 钩子（仅监听 EzCad2 进程）并泵消息直至超时：
    /// 该进程任何窗口一显示即被「最小化 → 工具窗样式 → 隐藏」，毫秒级响应，闪屏不闪现；
    /// 有标题的窗口记为主窗口（供退出时投递 WM_CLOSE）。
    /// </summary>
    private void StartShowEventWatcher(int pid, int timeoutMs, CancellationToken stoppingToken)
    {
        // 委托须存字段防 GC 回收（钩子持有裸函数指针）
        _showEventCallback = OnShowEvent;
        _watchedPid = pid;

        using var watcherDone = new ManualResetEventSlim();
        _watcherThread = new Thread(() =>
        {
            var hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero,
                _showEventCallback, (uint)pid, 0, WINEVENT_OUTOFCONTEXT);
            if (hook == IntPtr.Zero)
            {
                _logger.LogWarning("WinEvent 钩子安装失败，回退为仅靠轮询隐藏窗口");
            }

            watcherDone.Set();
            // 消息泵：钩子回调经本线程消息队列派发；WM_QUIT 退出
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (hook != IntPtr.Zero)
            {
                UnhookWinEvent(hook);
            }
        });
        _watcherThread.IsBackground = true;
        _watcherThread.Start();
        watcherDone.Wait();

        // 等待窗口就绪：找到主窗口后再短留 2 秒（让工程文件加载完）即提前结束，避免钩子长期与
        // EzCad2 的重复显示窗口行为互相争抢（显示→隐藏→显示……造成任务栏图标持续闪烁）
        var deadline = Environment.TickCount64 + timeoutMs;
        var minWaitUntil = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline && !stoppingToken.IsCancellationRequested)
        {
            if (_mainWindowHandle != IntPtr.Zero && IsWindow(_mainWindowHandle) && Environment.TickCount64 >= minWaitUntil)
            {
                break;
            }

            Thread.Sleep(100);
        }

        if (_watcherThread.IsAlive)
        {
            // 投递 WM_QUIT 结束消息泵；线程可能尚未创建消息队列导致投递失败，重试数次
            for (var attempt = 0; attempt < 5 && _watcherThread.IsAlive; attempt++)
            {
                if (PostThreadMessage(_watcherThread.ManagedThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero))
                {
                    break;
                }

                Thread.Sleep(100);
            }

            _watcherThread.Join(2000);
        }
    }

    /// <summary>SHOW 事件回调：目标进程的窗口一显示立即隐藏（保留有标题窗口为主窗口句柄）。</summary>
    private void OnShowEvent(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 /* OBJID_WINDOW */ || hwnd == IntPtr.Zero)
        {
            return;
        }

        GetWindowThreadProcessId(hwnd, out var windowPid);
        if (windowPid != _watchedPid)
        {
            return;
        }

        // 主窗口优先取有标题的（闪屏为无标题无边框窗口）
        if (_mainWindowHandle == IntPtr.Zero && GetWindowTextLength(hwnd) > 0)
        {
            _mainWindowHandle = hwnd;
        }

        if (!_hideWindow)
        {
            // 调试期配置：窗口正常显示，钩子只记录主窗口句柄，绝不隐藏（否则与程序反复显示窗口形成闪烁循环）
            return;
        }

        // 强制最小化 → 工具窗样式（任务栏无图标）→ 隐藏
        ShowWindow(hwnd, SW_MINIMIZE);
        ApplyToolWindowStyle(hwnd);
        ShowWindow(hwnd, SW_HIDE);
    }

    /// <summary>WinEvent 钩子回调委托（须保活防 GC）。</summary>
    private WinEventDelegate? _showEventCallback;

    /// <summary>钩子监听的目标进程 PID。</summary>
    private int _watchedPid;

    /// <summary>钩子消息泵线程。</summary>
    private Thread? _watcherThread;

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
    /// 构造启动参数：指定的 .ezd 文件存在时作为参数传入（含引号防路径空格）；
    /// 文件不存在仅记录警告，仍正常启动软件（不阻断打标软件可用性）。
    /// </summary>
    private string ResolveEzdArgument(string ezdFilePath)
    {
        if (string.IsNullOrWhiteSpace(ezdFilePath))
        {
            return string.Empty;
        }

        if (!File.Exists(ezdFilePath))
        {
            _logger.LogWarning("配置的 Ezd 工程文件不存在，启动时不加载：{EzdPath}", ezdFilePath);
            return string.Empty;
        }

        return $"\"{ezdFilePath}\"";
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
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    // WinEvent 钩子（EVENT_OBJECT_SHOW：窗口显示事件；WINEVENT_OUTOFCONTEXT：跨进程回调到本线程）
    private delegate void WinEventDelegate(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(int threadId, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_CLOSE = 0x0010;
    private const uint WM_QUIT = 0x0012;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern long GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern long SetWindowLong(IntPtr hWnd, int nIndex, long dwNewLong);

}
