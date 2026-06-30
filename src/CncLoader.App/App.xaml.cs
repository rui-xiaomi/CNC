using System.Windows;
using System.Windows.Threading;
using CncLoader.App.Startup;
using CncLoader.Common.Configuration;
using CncLoader.Common.DependencyInjection;
using CncLoader.Common.Logging;
using CncLoader.Communication.DependencyInjection;
using CncLoader.Communication.Plc;
using CncLoader.Core.DependencyInjection;
using CncLoader.Core.Polling;
using CncLoader.Data.DependencyInjection;
using CncLoader.Data.Repositories;
using CncLoader.UI.DependencyInjection;
using CncLoader.UI.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace CncLoader.App;

public partial class App : Application
{
    private IHost? _host;
    private Microsoft.Extensions.Logging.ILogger? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        try
        {
            _host = BuildHost();
            await _host.StartAsync();
            _logger = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

            // 先显示主窗口，再后台跑启动自检（DB 探活 + PLC 建链 + 轮询一轮）。
            // 自检可能因连不上真机而耗时数十秒，绝不能阻塞窗口显示。
            var shell = _host.Services.GetRequiredService<ShellWindow>();
            shell.Show();

            _ = RunStartupVerificationSafeAsync(_host.Services);
        }
        catch (Exception ex)
        {
            Log.Logger.Fatal(ex, "应用启动失败");
            MessageBox.Show($"应用启动失败：\n{ex.Message}", "CNC 自动化上下料客户端",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static IHost BuildHost()
    {
        var baseDir = AppContext.BaseDirectory;

        var configuration = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        var appOptions = configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();
        var serilog = LoggerSetup.Create(appOptions.Logging, baseDir);
        Log.Logger = serilog;

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(serilog, dispose: true);

        builder.Services.AddCncCommon(configuration);
        builder.Services.AddCncCore();
        builder.Services.AddCncData();
        builder.Services.AddCncCommunication();
        builder.Services.AddCncUi();
        builder.Services.AddSingleton<PlcRuntimeBootstrapper>();

        return builder.Build();
    }

    /// <summary>
    /// Phase 1 启动自检：数据库连通 + ORM 映射、PLC 模拟器建链、轮询读一轮、读单寄存器。
    /// 全程尽力而为：任一步骤失败仅记日志，绝不阻止主窗口显示（DB 未就绪时仍可演示界面与模拟器通信）。
    /// </summary>
    /// <summary>后台执行启动自检；任何异常仅记日志，不影响已显示的主窗口。</summary>
    private async Task RunStartupVerificationSafeAsync(IServiceProvider sp)
    {
        try { await RunStartupVerificationAsync(sp); }
        catch (Exception ex) { _logger!.LogWarning(ex, "启动自检异常"); }
    }

    private async Task RunStartupVerificationAsync(IServiceProvider sp)
    {
        // 1. 数据库连通与 ORM 映射
        try
        {
            var probe = sp.GetRequiredService<IDataHealthProbe>();
            var result = await probe.ProbeAsync();
            if (result.Connected)
                _logger!.LogInformation(
                    "数据库连通 ✓ 线体={WL} PLC={PLC} 机台={EQ} 加工位={POS} 点位={PT} 料架={FR}",
                    result.WorkLines, result.Plcs, result.Equipments, result.Positions, result.PlcPoints, result.Frames);
            else
                _logger!.LogWarning("数据库未连通：{Error}（请先执行 docs/sql/cnc_schema.sql 建库并核对 appsettings 连接配置）", result.Error);
        }
        catch (Exception ex) { _logger!.LogWarning(ex, "数据库自检异常"); }

        // 2. PLC 运行时（模拟器）装配 + 建链
        try { await sp.GetRequiredService<PlcRuntimeBootstrapper>().InitializeAsync(); }
        catch (Exception ex) { _logger!.LogWarning(ex, "PLC 运行时装配异常"); }

        // 3. 轮询读一轮（依赖 DB 点位；DB 未就绪时跳过，不影响通信链路验证）
        try
        {
            var polling = sp.GetRequiredService<IPlcPollingService>();
            var read = await polling.PollOnceAsync();
            _logger!.LogInformation("轮询一轮完成，读到 {Count} 个点位。", read);
        }
        catch (Exception ex) { _logger!.LogWarning(ex, "轮询一轮跳过（通常因 DB 未就绪，点位来源不可用）"); }

        // 4. 端到端：直接读一个 D 寄存器验证通信链路（不依赖 DB）
        try
        {
            var connections = sp.GetRequiredService<PlcConnectionManager>();
            var client = connections.All.FirstOrDefault(c => c.IsConnected);
            if (client is not null)
            {
                var values = await client.ReadRegistersAsync("D1006", 1);
                _logger!.LogInformation("端到端验证 ✓ PLC#{PlcId} 读 D1006 = {Value}", client.PlcId, values.FirstOrDefault());
            }
            else
            {
                _logger!.LogWarning("无已连接 PLC，跳过端到端读寄存器验证。");
            }
        }
        catch (Exception ex) { _logger!.LogWarning(ex, "端到端读寄存器失败"); }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnTaskException;
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Logger.Error(e.Exception, "UI 线程未处理异常");
        MessageBox.Show($"发生未处理异常：\n{e.Exception.Message}", "CNC 自动化上下料客户端",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true; // 不崩溃退出
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        => Log.Logger.Error(e.ExceptionObject as Exception, "AppDomain 未处理异常");

    private void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Logger.Error(e.Exception, "未观察的 Task 异常");
        e.SetObserved();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _host.StopAsync(cts.Token);
                _host.Dispose();
            }
        }
        catch (Exception ex) { Log.Logger.Warning(ex, "停止主机异常"); }
        finally { Log.CloseAndFlush(); }
        base.OnExit(e);
    }
}
