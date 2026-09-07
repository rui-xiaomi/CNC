using System.Diagnostics;
using CncLoader.Common.Configuration;
using CncLoader.Communication.Plc;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;
using CncLoader.Core.Signals;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.Plc;

public sealed class PlcEndpointResolver : IPlcEndpointResolver
{
    private const int SimulatorPortBase = 15000;
    private const int FinsSimulatorPortBase = 16000;
    private readonly PlcOptions _options;

    public PlcEndpointResolver(IOptions<AppOptions> options) => _options = options.Value.Plc;

    public PlcEndpoint Resolve(long plcId, string dbIp, int dbPort, string protocol = "ModbusTCP")
    {
        var isFins = protocol.Contains("FINS", StringComparison.OrdinalIgnoreCase);

        // 模拟器模式：按协议指向对应模拟器端口段（与 PlcRuntimeBootstrapper 一致）。
        if (_options.UseSimulator)
        {
            var simBase = isFins ? FinsSimulatorPortBase : SimulatorPortBase;
            return new PlcEndpoint(SimulatorLoopback.ResolvePlcBind(true, _options.SimulatorBindAddress),
                simBase + (int)plcId, protocol);
        }

        var defaultPort = isFins ? _options.FinsDefaultPort : _options.DefaultPort;
        return new PlcEndpoint(dbIp, dbPort <= 0 ? defaultPort : dbPort, protocol);
    }
}
