namespace CncLoader.Common.Configuration;

/// <summary>
/// 模拟器模式的环回地址。现场 IP 只用于真机联调；
/// <c>UseSimulator=true</c> 时出站与监听必须打本机，否则会连上现场 RCS/PLC。
/// </summary>
public static class SimulatorLoopback
{
    public const string Host = "127.0.0.1";
    public const int DefaultRcsPort = 8090;

    public static bool IsLoopback(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var trimmed = address.Trim();
        return trimmed is Host or "localhost" or "::1";
    }

    /// <summary>PLC 模拟器监听/连接地址。非环回配置在模拟器模式下强制改写。</summary>
    public static string ResolvePlcBind(bool useSimulator, string? configured)
    {
        if (!useSimulator)
            return string.IsNullOrWhiteSpace(configured) ? Host : configured.Trim();
        return Host;
    }

    /// <summary>把任意 RCS BaseUrl 改写为 <c>http://127.0.0.1:{port}</c>，端口沿用原 URL，缺省 8090。</summary>
    public static string ToLocalRcsBaseUrl(string? configured)
    {
        if (Uri.TryCreate(configured?.Trim(), UriKind.Absolute, out var uri)
            && uri.Port > 0)
            return $"http://{Host}:{uri.Port}";
        return $"http://{Host}:{DefaultRcsPort}";
    }
}
