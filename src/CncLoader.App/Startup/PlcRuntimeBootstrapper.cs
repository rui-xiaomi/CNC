using CncLoader.Common.Configuration;
using CncLoader.Communication.Plc;
using CncLoader.Communication.Simulation;
using CncLoader.Core.Signals;
using CncLoader.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.App.Startup;

/// <summary>
/// 启动时装配 PLC 运行时：按 DB 的 PLC/点位（无库时用内置默认）配置连接端点。
/// 开发期 UseSimulator=true 时为每台 PLC 启动一个环回模拟器端口并按信号表预置寄存器，
/// 随后建链——使 Phase 1 可端到端验证"连接 + 读寄存器"。
/// </summary>
public sealed class PlcRuntimeBootstrapper
{
    private const int SimulatorPortBase = 15000;

    private readonly IDbContextFactory<CncDbContext> _dbFactory;
    private readonly PlcConnectionManager _connections;
    private readonly ModbusTcpSimulator _simulator;
    private readonly PlcOptions _plc;
    private readonly ILogger<PlcRuntimeBootstrapper> _logger;

    public PlcRuntimeBootstrapper(IDbContextFactory<CncDbContext> dbFactory, PlcConnectionManager connections,
        ModbusTcpSimulator simulator, IOptions<AppOptions> options, ILogger<PlcRuntimeBootstrapper> logger)
    {
        _dbFactory = dbFactory;
        _connections = connections;
        _simulator = simulator;
        _plc = options.Value.Plc;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var plcs = await LoadPlcDefinitionsAsync(ct);
        if (plcs.Count == 0)
        {
            _logger.LogWarning("未找到任何 PLC 定义，跳过 PLC 运行时装配。");
            return;
        }

        if (_plc.UseSimulator)
        {
            foreach (var p in plcs)
            {
                var port = SimulatorPortBase + (int)p.PlcId;
                _simulator.AddPlc(p.PlcId, port, p.Seed);
            }
            await _simulator.StartAsync();

            foreach (var p in plcs)
            {
                var port = SimulatorPortBase + (int)p.PlcId;
                _connections.Register(p.PlcId, new PlcEndpoint(_plc.SimulatorBindAddress, port));
            }
            _logger.LogInformation("内置 Modbus 模拟器已启动，登记 {Count} 台虚拟 PLC。", plcs.Count);
        }
        else
        {
            foreach (var p in plcs)
                _connections.Register(p.PlcId, new PlcEndpoint(p.Host, p.Port));
        }

        await _connections.ConnectAllAsync(ct);
        var online = _connections.ConnectionStates.Count(kv => kv.Value);
        _logger.LogInformation("PLC 建链完成：{Online}/{Total} 在线。", online, plcs.Count);
    }

    private async Task<List<PlcDefinition>> LoadPlcDefinitionsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            if (await db.Database.CanConnectAsync(ct))
            {
                var plcs = await db.Plcs.AsNoTracking().Where(p => p.State == "0").ToListAsync(ct);
                var points = await db.PlcPoints.AsNoTracking().Where(p => p.State == "0").ToListAsync(ct);
                if (plcs.Count > 0)
                {
                    var result = new List<PlcDefinition>();
                    foreach (var plc in plcs)
                    {
                        var seed = new Dictionary<int, ushort>();
                        foreach (var pt in points.Where(x => x.PlcId == plc.PlcId))
                        {
                            var offset = RegisterAddress.ToRegisterIndex(pt.RegisterAddr);
                            // 读点位初值给 OFF；写点位给 0。
                            seed[offset] = pt.Rw == "1" ? (ushort)0 : (ushort)pt.OffValue;
                        }
                        result.Add(new PlcDefinition(plc.PlcId, plc.PlcComputerIp,
                            plc.PlcComputerPort ?? _plc.DefaultPort, seed));
                    }
                    return result;
                }
            }
            _logger.LogWarning("数据库不可用或无 PLC 记录，使用内置默认（仅内长宽 PLC）供模拟验证。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 PLC 定义失败，使用内置默认。");
        }

        return [BuildDefaultPlc()];
    }

    /// <summary>无库时的内置默认：内长宽 PLC（id=1），按信号表 D1000–D1102 预置读点位 OFF。</summary>
    private static PlcDefinition BuildDefaultPlc()
    {
        var seed = new Dictionary<int, ushort>();
        // 读点位（OFF=2）
        foreach (var addr in new[] { 1000, 1002, 1004, 1006, 1008, 1010, 1012, 1014, 1016, 1018 })
            seed[addr] = 2;
        // 写点位（启动信号，初值 0）
        seed[1100] = 0;
        seed[1102] = 0;
        return new PlcDefinition(1, "127.0.0.1", 502, seed);
    }

    private sealed record PlcDefinition(long PlcId, string Host, int Port, Dictionary<int, ushort> Seed);
}
