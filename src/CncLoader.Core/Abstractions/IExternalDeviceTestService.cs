namespace CncLoader.Core.Abstractions;

/// <summary>AGV 连通性测试服务（仅测试，不纳入调度）。</summary>
public interface IAgvTestService
{
    /// <summary>按配置发起一次测试连接，返回响应文本/状态码/耗时/异常。</summary>
    Task<AgvTestResult> TestConnectionAsync(AgvTestConfig config, CancellationToken ct = default);
}

/// <summary>扫码枪连通性监听服务（仅测试，不纳入来料校验）。</summary>
public interface IScanListenerService
{
    bool IsListening { get; }
    int ConnectedCount { get; }

    /// <summary>开始监听端口；重复调用幂等（已在监听则返回 false）。</summary>
    Task<bool> StartAsync(int port, CancellationToken ct = default);

    /// <summary>停止监听并断开所有连接。</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>新扫码到达事件（UI 订阅）。</summary>
    event Action<ScanRecord>? ScanReceived;

    event Action<int>? ConnectedCountChanged;

    /// <summary>最近 N 条扫码记录（倒序）。</summary>
    IReadOnlyList<ScanRecord> GetRecent(int max = 50);
}

/// <summary>AGV 测试配置（表单输入）。</summary>
public sealed class AgvTestConfig
{
    public string Name { get; set; } = "";
    public AgvCommWay CommWay { get; set; } = AgvCommWay.HttpRest;
    public string Endpoint { get; set; } = "http://192.168.1.30:8080";
}

public enum AgvCommWay { HttpRest, Socket }

/// <summary>AGV 测试结果。</summary>
public sealed record AgvTestResult(
    bool Connected,
    int StatusCode,
    int ElapsedMs,
    string? RawResponse,
    string? Error);

/// <summary>扫码记录。</summary>
public sealed record ScanRecord(DateTime Time, string Source, string Content);
