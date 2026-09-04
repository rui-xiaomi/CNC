using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CncLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.ExternalDevices;

/// <summary>扫码枪连通性监听：TCP 服务端模式被动接收扫码数据（按行/按结束符解析）。</summary>
public sealed class ScanListenerService : IScanListenerService, IDisposable
{
    private readonly ILogger<ScanListenerService> _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentQueue<ScanRecord> _recent = new();
    private const int MaxRecent = 50;
    private int _connectedCount;

    public ScanListenerService(ILogger<ScanListenerService> logger) => _logger = logger;

    public bool IsListening => _listener is not null;
    public int ConnectedCount => _connectedCount;

    public event Action<ScanRecord>? ScanReceived;
    public event Action<int>? ConnectedCountChanged;

    public Task<bool> StartAsync(int port, CancellationToken ct = default)
    {
        if (IsListening) return Task.FromResult(false);
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, Math.Clamp(port, 1, 65535));
            _listener.Start();
            _ = AcceptLoopAsync(_cts.Token);
            _logger.LogInformation("扫码枪监听已启动，端口 {Port}", port);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "扫码枪监听启动失败，端口 {Port}", port);
            _listener = null;
            return Task.FromResult(false);
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_listener is null) return;
        try
        {
            _cts?.Cancel();
            _listener.Stop();
            foreach (var c in _clients.Keys)
                try { c.Close(); } catch { }
            _clients.Clear();
            UpdateConnectedCount(0);
            _logger.LogInformation("扫码枪监听已停止");
        }
        finally
        {
            _listener = null;
            _cts?.Dispose();
            _cts = null;
        }
        await Task.CompletedTask;
    }

    public IReadOnlyList<ScanRecord> GetRecent(int max = 50)
    {
        var n = Math.Min(max, MaxRecent);
        return _recent.Take(n).ToList();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                // 真实监听故障：置 IsListening=false，避免 UI 误报「在监听」（P2-3）。
                _logger.LogWarning(ex, "扫码枪监听 accept 异常，停止监听");
                _listener = null;
                break;
            }
            _clients.TryAdd(client, 0);
            UpdateConnectedCount(_connectedCount + 1);
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var ep = client.Client.RemoteEndPoint?.ToString() ?? "?";
        try
        {
            using var stream = client.GetStream();
            var buf = new byte[1024];
            var sb = new StringBuilder();
            while (!ct.IsCancellationRequested && client.Connected)
            {
                var n = await stream.ReadAsync(buf, ct);
                if (n == 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                while (TryExtractLine(sb, out var line))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        var rec = new ScanRecord(DateTime.Now, ep, line.Trim());
                        _recent.Enqueue(rec);
                        TrimRecent();
                        ScanReceived?.Invoke(rec);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "扫码枪客户端 {Ep} 读取异常", ep); }
        finally
        {
            try { client.Close(); } catch { }
            _clients.TryRemove(client, out _); // 断开即摘除，避免关闭的 TcpClient 无界累积（P2-3）
            UpdateConnectedCount(Math.Max(0, _connectedCount - 1));
        }
    }

    private static bool TryExtractLine(StringBuilder sb, out string line)
    {
        // 常见扫码枪结束符：CR/LF 或 ETX(0x03)；这里按 \r/\n 拆分
        for (var i = 0; i < sb.Length; i++)
        {
            if (sb[i] == '\r' || sb[i] == '\n')
            {
                line = sb.ToString(0, i);
                sb.Remove(0, i + 1);
                while (sb.Length > 0 && (sb[0] == '\r' || sb[0] == '\n'))
                    sb.Remove(0, 1);
                return true;
            }
        }
        line = string.Empty;
        return false;
    }

    private void TrimRecent()
    {
        while (_recent.Count > MaxRecent && _recent.TryDequeue(out _)) { }
    }

    private void UpdateConnectedCount(int v)
    {
        _connectedCount = v;
        ConnectedCountChanged?.Invoke(v);
    }

    public void Dispose()
    {
        _ = StopAsync(default);
    }
}
