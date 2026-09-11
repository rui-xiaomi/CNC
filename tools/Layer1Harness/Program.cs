using System.Net.Http;
using System.Text;
using CncLoader.Common.Configuration;
using CncLoader.Common.DependencyInjection;
using CncLoader.Common.Logging;
using CncLoader.Communication.DependencyInjection;
using CncLoader.Core.DependencyInjection;
using CncLoader.Core.Rcs;
using CncLoader.Data.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using MsLog = Microsoft.Extensions.Logging.ILogger;

namespace Layer1Harness;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var mode = args.FirstOrDefault() ?? "all";
        Console.OutputEncoding = Encoding.UTF8;

        using var host = BuildHost();
        await host.StartAsync();
        await Task.Delay(2500);

        var svc = host.Services.GetRequiredService<IRcsTaskService>();
        var msgLog = host.Services.GetRequiredService<IRcsMessageLog>();
        var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Layer1");

        var failed = 0;
        failed += await RunClientTestsAsync(svc, msgLog, log, mode);
        failed += await RunCallbackTestsAsync(log, mode);
        if (mode is "all" or "tracker")
            failed += await RunTrackerTestsAsync(host.Services, log);

        await host.StopAsync();
        Console.WriteLine(failed == 0 ? "\n=== 第一层全部通过 ===" : $"\n=== 失败 {failed} 项 ===");
        return failed == 0 ? 0 : 1;
    }

    private static IHost BuildHost()
    {
        var baseDir = AppContext.BaseDirectory;
        Environment.SetEnvironmentVariable("App__Rcs__SchedulerEnabled", "false");
        Environment.SetEnvironmentVariable("App__Plc__UseSimulator", "false");
        Environment.SetEnvironmentVariable("App__Plc__PollingEnabled", "false");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: false)
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

        return builder.Build();
    }

    private static async Task<int> RunClientTestsAsync(IRcsTaskService svc, IRcsMessageLog msgLog, MsLog log, string mode)
    {
        if (mode is not ("all" or "client")) return 0;
        var failed = 0;
        Console.WriteLine("\n--- 1.1 RcsClient 四出站 + 报文流水 ---");

        var transit = await svc.DispatchTransitAsync(new TransitDispatchArgs
        {
            WorkLineId = 1, LineCode = "LINE01", FromCode = "601203", ToCode = "603201", Author = "layer1"
        });
        Assert(log, "transitTask 应答 OK", transit.Ok, ref failed);
        var transitId = transit.TaskId!;

        var grab = await svc.DispatchGrabAsync(new GrabDispatchArgs
        {
            WorkLineId = 1, LineCode = "LINE01", SrcStation = "101", DstStation = "201",
            Items = [new GrabItem { SrcNo = 101, SrcPos = 101, DstNo = 201, DstPos = 101, Data = "EL-TEST" }],
            Author = "layer1"
        });
        Assert(log, "grabTask(excute) 应答 OK", grab.Ok, ref failed);

        var identify = await svc.DispatchIdentifyAsync(new IdentifyDispatchArgs
        {
            WorkLineId = 1, LineCode = "LINE01", Station = "101", PosStart = 101, Count = 3, Author = "layer1"
        });
        Assert(log, "identifyQR(excute) 应答 OK", identify.Ok, ref failed);
        var identifyId = identify.TaskId!;

        await Task.Delay(5000);

        var query = await svc.QueryAsync(new QueryTaskRequest
        {
            Condition = new QueryCondition
            {
                Conditions = [new QueryConditionItem { Key = QueryTaskRequest.IdKey, Value = transitId, Operator = "IN" }]
            }
        });
        Assert(log, "queryTask 应答 OK", query.Ok, ref failed);

        var cancel = await svc.CancelAsync(transitId);
        Assert(log, "cancelTask 应答 OK", cancel.Ok, ref failed);

        var outMsgs = await msgLog.QueryAsync(new RcsMsgQuery { Direction = "OUT", Limit = 50 });
        var ifaces = outMsgs.Select(m => m.Interface).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert(log, "OUT 流水含 transitTask", ifaces.Contains("transitTask"), ref failed);
        Assert(log, "OUT 流水含 excuteTask", ifaces.Contains("excuteTask"), ref failed);
        Assert(log, "OUT 流水含 cancelTask", ifaces.Contains("cancelTask"), ref failed);
        Assert(log, "OUT 流水含 queryTask", ifaces.Contains("queryTask"), ref failed);

        await Task.Delay(3000);
        var identifyRow = (await svc.GetRecentTasksAsync(20)).FirstOrDefault(t => t.RcsTaskId == identifyId);
        Assert(log, "identify 任务终态 COMPLETED", identifyRow?.TaskState == RcsTaskState.Completed, ref failed);

        return failed;
    }

    private static async Task<int> RunCallbackTestsAsync(MsLog log, string mode)
    {
        if (mode is not ("all" or "callback")) return 0;
        var failed = 0;
        Console.WriteLine("\n--- 1.2 回调服务端（curl 模拟 RCS 推报文）---");

        const int callbackPort = 9080;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var manualTaskId = $"LAYER1-MANUAL-{DateTime.Now:yyyyMMddHHmmss}";

        var pushBody = $@"{{""taskId"":""{manualTaskId}"",""version"":""1.0.0"",""data"":{{""system"":{{""error_code"":0,""msg"":""ok""}}}}}}";
        var (r1, b1) = await PostAsync(http, $"http://127.0.0.1:{callbackPort}/externalApi/pushTaskStatus", pushBody);
        Assert(log, "pushTaskStatus 首次 200", r1, ref failed);
        Assert(log, "pushTaskStatus 应答含 taskId", b1.Contains(manualTaskId, StringComparison.Ordinal), ref failed);

        var (r2, _) = await PostAsync(http, $"http://127.0.0.1:{callbackPort}/externalApi/pushTaskStatus", pushBody);
        Assert(log, "pushTaskStatus 重复推送仍 200（幂等）", r2, ref failed);

        var scanBody = $@"{{""taskId"":""{manualTaskId}"",""version"":""1.0.0"",""data"":{{""code"":""101"",""system"":{{""error_code"":0,""msg"":""ok""}},""products"":[""A1"",""A2"",""A3""]}}}}";
        var (r3, b3) = await PostAsync(http, $"http://127.0.0.1:{callbackPort}/externalApi/scanTaskStatus", scanBody);
        Assert(log, "scanTaskStatus 200", r3, ref failed);
        Assert(log, "scanTaskStatus 应答含 taskId", b3.Contains(manualTaskId, StringComparison.Ordinal), ref failed);

        const string warnBody = """{"version":"1.0.0","data":[{"robotCode":"R01","beginTime":"2026-07-08 16:00:00","warnContent":"LAYER1测试告警","taskCode":"T001"}]}""";
        var (r4, b4) = await PostAsync(http, $"http://127.0.0.1:{callbackPort}/externalApi/warnCallback", warnBody);
        Assert(log, "warnCallback 200", r4, ref failed);
        Assert(log, "warnCallback 应答 taskId 空串", b4.Contains("\"taskId\":\"\"", StringComparison.Ordinal) || b4.Contains("\"taskId\": \"\"", StringComparison.Ordinal), ref failed);

        var (r5, _) = await PostAsync(http, $"http://127.0.0.1:{callbackPort}/externalApi/warnCallback", warnBody);
        Assert(log, "warnCallback 重复推送幂等（仍 200）", r5, ref failed);

        return failed;
    }

    private static async Task<int> RunTrackerTestsAsync(IServiceProvider sp, MsLog log)
    {
        var failed = 0;
        Console.WriteLine("\n--- 1.3 任务跟踪（失败/redo/取消）---");

        var options = sp.GetRequiredService<IOptions<AppOptions>>().Value.Rcs;
        if (options.SimulatorFailureRate < 1.0 && options.SimulatorCancelRate < 1.0)
        {
            log.LogWarning("跳过 tracker 自动断言：SimulatorFailureRate/CancelRate 未设为 1.0");
            Console.WriteLine("  [SKIP] 失败/redo 链：设 App__Rcs__SimulatorFailureRate=1.0 后重跑");
            Console.WriteLine("  [SKIP] 取消链：设 App__Rcs__SimulatorCancelRate=1.0 后重跑");
            return 0;
        }

        var svc = sp.GetRequiredService<IRcsTaskService>();

        if (options.SimulatorFailureRate >= 1.0)
        {
            var r = await svc.DispatchTransitAsync(new TransitDispatchArgs
            {
                WorkLineId = 1, LineCode = "LINE01", FromCode = "601203", ToCode = "603201", Author = "layer1-fail"
            });
            var taskId = r.TaskId!;
            await Task.Delay(20000);

            var row = (await svc.GetRecentTasksAsync(20)).First(t => t.RcsTaskId == taskId);
            Assert(log, "失败任务 REDO_COUNT>=3", row.RedoCount >= 3, ref failed);
            Assert(log, "失败任务终态 FAILED", row.TaskState == RcsTaskState.Failed, ref failed);
            Assert(log, "自动 redo 使用同一 taskId", row.RcsTaskId == taskId, ref failed);

            var before = row.RedoCount;
            await svc.RedoAsync(taskId);
            var after = (await svc.GetRecentTasksAsync(20)).First(t => t.RcsTaskId == taskId);
            Assert(log, "手动 redo 仍用同一 taskId", after.RcsTaskId == taskId, ref failed);
            Assert(log, "手动 redo 会递增 REDO_COUNT", after.RedoCount > before, ref failed);
        }

        if (options.SimulatorCancelRate >= 1.0)
        {
            var r = await svc.DispatchTransitAsync(new TransitDispatchArgs
            {
                WorkLineId = 1, LineCode = "LINE01", FromCode = "601203", ToCode = "603201", Author = "layer1-cancel"
            });
            var taskId = r.TaskId!;
            await Task.Delay(6000);
            var row = (await svc.GetRecentTasksAsync(20)).First(t => t.RcsTaskId == taskId);
            Assert(log, "取消任务终态 CANCELED", row.TaskState == RcsTaskState.Canceled, ref failed);
        }

        return failed;
    }

    private static async Task<(bool ok, string body)> PostAsync(HttpClient http, string url, string json)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(url, content);
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, body);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static void Assert(MsLog log, string name, bool cond, ref int failed)
    {
        if (cond)
        {
            Console.WriteLine($"  [PASS] {name}");
            log.LogInformation("PASS: {Name}", name);
        }
        else
        {
            Console.WriteLine($"  [FAIL] {name}");
            log.LogError("FAIL: {Name}", name);
            failed++;
        }
    }
}
