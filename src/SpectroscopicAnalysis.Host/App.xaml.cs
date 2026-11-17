using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SpectroscopicAnalysis.Host.LoggingAlarm;
using SpectroscopicAnalysis.Host.Data;
using SpectroscopicAnalysis.Host.Data.Database;
using SpectroscopicAnalysis.Host.Data.Repositories;
// 命名空间尾段 Host 会遮蔽 M.E.Hosting 的 Host 类，起别名引用
using HostApp = Microsoft.Extensions.Hosting.Host;

namespace SpectroscopicAnalysis.Host;

/// <summary>
/// 应用入口：Generic Host + DI 骨架（开发计划 D1/D3）。
/// 依赖一律经构造注入；日志统一走 Serilog；数据库启动时自动建库（WAL + 外键）。
/// </summary>
public partial class App
{
    private readonly IHost _host;

    /// <summary>
    /// 判断是否已有「可用」的上位机实例在运行：同名进程存在且拥有可见主窗口。
    /// 按窗口判断而非互斥体——强杀残留的僵尸进程（无窗口）不应阻止新实例启动。
    /// </summary>
    private static bool HasVisibleRunningInstance()
    {
        var currentId = Environment.ProcessId;
        var exeName = Environment.ProcessPath is { } path ? System.IO.Path.GetFileNameWithoutExtension(path) : null;

        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            if (process.Id == currentId)
            {
                continue;
            }

            var nameMatches = exeName != null
                ? string.Equals(process.ProcessName, exeName, StringComparison.OrdinalIgnoreCase)
                : process.ProcessName.Contains("SpectroscopicAnalysis", StringComparison.OrdinalIgnoreCase);
            if (nameMatches && process.MainWindowHandle != IntPtr.Zero)
            {
                return true;
            }
        }

        return false;
    }

    public App()
    {
        _host = HostApp.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                // 数据库文件路径：配置的相对路径基于应用基目录解析（Data/ 不入库，见 .gitignore）
                var dbPath = Path.Combine(AppContext.BaseDirectory,
                    context.Configuration["Database:Path"] ?? Path.Combine("Data", "spectro.db"));

                services.AddSingleton(new Db(dbPath));
                services.AddSingleton<DbInitializer>();
                services.AddSingleton<ISctSampleRepository, SctSampleRepository>();
                services.AddSingleton<IProductionSampleRepository, ProductionSampleRepository>();
                // 子表仓储（样品档案页「切换数据表」；前两者可删除，后两者只读）
                services.AddSingleton<ISctCertifiedValueRepository, SctCertifiedValueRepository>();
                services.AddSingleton<IControlLimitRepository, ControlLimitRepository>();
                services.AddSingleton<ICorrectionCoefficientRepository, CorrectionCoefficientRepository>();
                services.AddSingleton<ISctOperationLogRepository, SctOperationLogRepository>();
                // SCT 策略配置存取 + 策略引擎（纯决策：触发判断/选样/优先级队列/超差闭环/测高联动）
                services.AddSingleton<SctStrategy.ISctPolicyStore, SctStrategy.JsonSctPolicyStore>();
                services.AddSingleton<SctStrategy.ISctStrategyEngine, SctStrategy.SctStrategyEngine>();

                // 设备抽象 + Workflow 编排（Stub 网关；Modbus 真实现落地后仅替换本节注册）
                var workflowOptions = new Workflow.WorkflowOptions();
                context.Configuration.GetSection("Workflow").Bind(workflowOptions);
                services.AddSingleton(workflowOptions);
                services.AddSingleton<DeviceInterface.StubDeviceGateway>();
                services.AddSingleton<DeviceInterface.ISendTask>(sp => sp.GetRequiredService<DeviceInterface.StubDeviceGateway>());
                services.AddSingleton<DeviceInterface.IGetTaskStatus>(sp => sp.GetRequiredService<DeviceInterface.StubDeviceGateway>());
                services.AddSingleton<DeviceInterface.IGetSafetyStatus>(sp => sp.GetRequiredService<DeviceInterface.StubDeviceGateway>());
                services.AddSingleton<DeviceInterface.ISpectrometer>(sp => sp.GetRequiredService<DeviceInterface.StubDeviceGateway>());
                services.AddSingleton<DeviceInterface.IHeartbeat>(sp => sp.GetRequiredService<DeviceInterface.StubDeviceGateway>());
                services.AddSingleton<DeviceInterface.INotifyTaskEnd>(sp => sp.GetRequiredService<DeviceInterface.StubDeviceGateway>());
                services.AddSingleton<Workflow.IWorkflowOrchestrator, Workflow.WorkflowOrchestrator>();
                services.AddSingleton<SeedData>();

                // UI 层：调度抽象（MVVM 分层要求）+ 页面 ViewModel（单例，导航共享状态）
                services.AddSingleton<Ui.IUiDispatcher, Ui.UiDispatcher>();
                services.AddSingleton<IErrorCollector, ErrorCollector>();
                services.AddSingleton<Ui.IDialogService, Ui.DialogService>();
                services.AddSingleton<ViewModels.DashboardViewModel>();
                services.AddSingleton<ViewModels.SamplesViewModel>();
                services.AddSingleton<ViewModels.SctViewModel>();
                services.AddSingleton<ViewModels.CoefficientsViewModel>();
                services.AddSingleton<ViewModels.AlarmsViewModel>();
                services.AddSingleton<ViewModels.SettingsViewModel>();
                services.AddSingleton<ViewModels.LaserMarkingViewModel>();
                // 激光打标：打标文本内容读取服务（页面经接口消费，后续打标指令下发在同接口扩展）
                services.AddSingleton<Vision.ILaserMarkingService, Vision.LaserMarkingService>();
                services.AddSingleton<ViewModels.MainViewModel>();
                services.AddSingleton<MainWindow>();
                // 外部打标软件：随上位机启动自动拉起 EzCad2（最小化 + 不显示任务栏）
                // 单例双注册：既是托管服务（启动拉起/守护/退出联动关闭），又作为控制接口供打标方式切换调用
                services.AddSingleton<Vision.EzCad2Launcher>();
                services.AddHostedService(sp => sp.GetRequiredService<Vision.EzCad2Launcher>());
                services.AddSingleton<Vision.IEzCad2Controller>(sp => sp.GetRequiredService<Vision.EzCad2Launcher>());
            })
            .UseSerilog((context, loggerConfiguration) =>
            {
                loggerConfiguration
                    .MinimumLevel.Information()
                    .WriteTo.Console()
                    .WriteTo.File("logs/host-.log", rollingInterval: RollingInterval.Day);
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例保护：按「可见窗口」判断已有实例（僵尸进程无窗口不算），有实例时提示并退出
        if (HasVisibleRunningInstance())
        {
            MessageBox.Show("上位机已在运行中，请勿重复启动。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            Log.Information("检测到已有上位机实例运行，本次启动退出");
            Log.CloseAndFlush();
            Shutdown(0);
            return;
        }

        // 全局异常兜底（开发规范 11.2 最小版；完整错误链随 D9 落地）——启动期先记录再终止
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Fatal(ex.Exception, "UI 线程未处理异常");
            Log.CloseAndFlush();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Fatal(ex.ExceptionObject as Exception, "非托管/后台线程未处理异常");
            Log.CloseAndFlush();
        };

        _host.Start();

        // 建库（幂等，轻量）——失败记录并终止启动（启动期硬失败）
        var dbInitializer = _host.Services.GetRequiredService<DbInitializer>();
        dbInitializer.EnsureCreated();

        // Workflow 状态机自检（转移表写错立即暴露，编排设计文档 §4）+ 主循环启动（Stub 设备）
        Workflow.TaskState.ValidateTransitions();
        _ = _host.Services.GetRequiredService<Workflow.IWorkflowOrchestrator>().StartAsync(CancellationToken.None);

        // 主窗口由 DI 提供；DataContext = MainViewModel（组合根装配，View 零业务）
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _host.Services.GetRequiredService<ViewModels.MainViewModel>();
        // 先显示窗口再补种子：种子写入（数万行）在后台线程执行，避免 UI 线程阻塞导致启动无响应
        mainWindow.Show();
        Log.Information("上位机启动完成");

        try
        {
            var seeded = _host.Services.GetRequiredService<SeedData>();
            await Task.Run(seeded.SeedIfEmpty);
            await Task.Run(seeded.SeedCoefficientsIfEmpty);
            Log.Information("数据库就绪（建表/种子幂等完成）");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "种子数据写入失败，终止启动");
            Log.CloseAndFlush();
            Shutdown(-1);
            return;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Host 停止限时 5 秒：放到后台线程执行（.NET 10 宿主 StopAsync 在个别场景下即使有取消令牌也不返回，
        // 必须用硬超时兜底）；超时则直接强制退出进程——EzCad2 清理在服务 StopAsync 内部先行完成，不受影响
        var stopTask = Task.Run(() => _host.StopAsync(TimeSpan.FromSeconds(5)));
        if (!stopTask.Wait(TimeSpan.FromSeconds(5)))
        {
            Log.Warning("Host 停止超时（5 秒），强制退出进程");
            Log.CloseAndFlush();
            Environment.Exit(e.ApplicationExitCode);
        }

        _host.Dispose();
        Log.Information("上位机退出");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
