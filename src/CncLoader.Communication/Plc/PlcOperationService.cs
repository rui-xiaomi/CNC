using System.Diagnostics;
using CncLoader.Communication.Plc;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;
using CncLoader.Core.Signals;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.Plc;

public sealed class PlcOperationService : IPlcOperationService
{
    private readonly PlcConnectionManager _connections;
    private readonly IPlcPointSource _points;
    private readonly IDeviceLogStore _logStore;
    private readonly IAlarmEventService _alarms;
    private readonly ILogger<PlcOperationService> _logger;

    public PlcOperationService(PlcConnectionManager connections, IPlcPointSource points,
        IDeviceLogStore logStore, IAlarmEventService alarms, ILogger<PlcOperationService> logger)
    {
        _connections = connections;
        _points = points;
        _logStore = logStore;
        _alarms = alarms;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PlcReadResult>> ReadPointsAsync(long plcId, bool readOnlySignals = true, CancellationToken ct = default)
    {
        var client = RequireClient(plcId);
        var allPoints = await _points.GetByPlcAsync(plcId, ct);
        var points = readOnlySignals ? allPoints.Where(p => !p.IsWrite).ToList() : allPoints.ToList();
        var results = new List<PlcReadResult>(points.Count);

        // 未连接：整批失败，只告警一次（避免每个寄存器刷屏 Growl）。
        if (!client.IsConnected)
        {
            foreach (var p in points)
            {
                results.Add(new PlcReadResult(
                    null, SignalLabels.Get(p.Signal), null, p.RegisterAddress, 0,
                    "未连接", false, 0, $"PLC {plcId} 未连接"));
            }
            if (points.Count > 0)
                await _alarms.RaisePlcAlarmAsync(plcId, $"PLC#{plcId} 未连接，跳过读取 {points.Count} 个点位");
            return results;
        }

        string? firstError = null;
        var failCount = 0;
        foreach (var p in points)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var raw = await client.ReadRegistersAsync(p.RegisterAddress, p.DataLength, ct);
                var value = raw[0];
                var on = SignalConventions.Interpret(value, p);
                results.Add(new PlcReadResult(
                    null, SignalLabels.Get(p.Signal), null, p.RegisterAddress, value,
                    SignalLabels.Translate(p.Signal, value, p.OnValue, p.OffValue),
                    on == true, (int)sw.ElapsedMilliseconds, null));
            }
            catch (Exception ex)
            {
                failCount++;
                firstError ??= ex.Message;
                results.Add(new PlcReadResult(
                    null, SignalLabels.Get(p.Signal), null, p.RegisterAddress, 0,
                    "读取失败", false, (int)sw.ElapsedMilliseconds, ex.Message));
            }
        }

        if (failCount > 0)
            await _alarms.RaisePlcAlarmAsync(plcId, $"读点位失败 {failCount}/{points.Count}：{firstError}");

        return results;
    }

    public async Task<PlcReadResult> ReadRegisterAsync(long plcId, string registerAddress, int length, CancellationToken ct = default)
    {
        var client = RequireClient(plcId);
        var sw = Stopwatch.StartNew();
        try
        {
            var raw = await client.ReadRegistersAsync(registerAddress, length, ct);
            var value = raw[0];
            return new PlcReadResult(null, registerAddress, null, registerAddress, value,
                value.ToString(), false, (int)sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            await _alarms.RaisePlcAlarmAsync(plcId, $"读 {registerAddress} 失败：{ex.Message}");
            return new PlcReadResult(null, registerAddress, null, registerAddress, 0,
                "读取失败", false, (int)sw.ElapsedMilliseconds, ex.Message);
        }
    }

    public async Task<PlcWriteResult> WriteWithConfirmAsync(long plcId, string registerAddress, int value, string author, CancellationToken ct = default)
    {
        var client = RequireClient(plcId);
        var sw = Stopwatch.StartNew();

        // 先落流水（写操作审计要求）
        var logId = await _logStore.AppendAsync(new DeviceLogEntry
        {
            DeviceType = DeviceType.Plc,
            DeviceId = plcId,
            Action = DeviceAction.Write,
            RegisterAddress = registerAddress,
            Request = value.ToString(),
            Response = "PENDING",
            Success = false,
            Author = author
        }, ct);

        try
        {
            await client.WriteRegisterAsync(registerAddress, value, ct);
            var readback = await client.ReadRegistersAsync(registerAddress, 1, ct);
            var verified = readback[0] == value;
            var cost = (int)sw.ElapsedMilliseconds;
            await _logStore.UpdateAsync(logId, readback[0].ToString(), true, cost, null, ct);
            return new PlcWriteResult(registerAddress, value, readback[0], verified, cost, null);
        }
        catch (Exception ex)
        {
            var cost = (int)sw.ElapsedMilliseconds;
            await _logStore.UpdateAsync(logId, null, false, cost, ex.Message, ct);
            await _alarms.RaisePlcAlarmAsync(plcId, $"写 {registerAddress}={value} 失败：{ex.Message}");
            return new PlcWriteResult(registerAddress, value, null, false, cost, ex.Message);
        }
    }

    public async Task<PlcWriteResult> VerifyWriteAsync(long plcId, string registerAddress, int expectedValue, CancellationToken ct = default)
    {
        var client = RequireClient(plcId);
        var sw = Stopwatch.StartNew();
        try
        {
            var readback = await client.ReadRegistersAsync(registerAddress, 1, ct);
            return new PlcWriteResult(registerAddress, expectedValue, readback[0],
                readback[0] == expectedValue, (int)sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            return new PlcWriteResult(registerAddress, expectedValue, null, false,
                (int)sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private IPlcClient RequireClient(long plcId) =>
        _connections.Get(plcId) ?? throw new InvalidOperationException($"PLC#{plcId} 未登记");
}
