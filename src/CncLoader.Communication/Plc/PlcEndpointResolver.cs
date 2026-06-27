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
    private readonly PlcOptions _options;

    public PlcEndpointResolver(IOptions<AppOptions> options) => _options = options.Value.Plc;

    public PlcEndpoint Resolve(long plcId, string dbIp, int dbPort) =>
        _options.UseSimulator
            ? new PlcEndpoint(_options.SimulatorBindAddress, SimulatorPortBase + (int)plcId)
            : new PlcEndpoint(dbIp, dbPort <= 0 ? _options.DefaultPort : dbPort);
}
