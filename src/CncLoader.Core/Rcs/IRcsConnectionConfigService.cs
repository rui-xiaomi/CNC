namespace CncLoader.Core.Rcs;

/// <summary>RCS 连接配置（落库 MAS_AUTO_WORKLINE_AGV，页面可编辑）。</summary>
public interface IRcsConnectionConfigService
{
    /// <summary>读取当前启用配置；无行返回 null（调用方用 appsettings 兜底）。</summary>
    Task<RcsConnectionConfig?> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// 按线体 AGV_ID 取配置；无匹配则回退 <see cref="GetAsync"/>。
    /// </summary>
    Task<RcsConnectionConfig?> GetByWorkLineAgvIdAsync(long agvId, CancellationToken ct = default);

    /// <summary>保存（UPDATE 或 INSERT）；返回落库后的完整配置（含 Id）。</summary>
    Task<RcsConnectionConfig> SaveAsync(RcsConnectionConfig config, string author, CancellationToken ct = default);
}

/// <summary>RCS 连接配置 DTO（与页面可编辑字段一致）。</summary>
public sealed class RcsConnectionConfig
{
    public long Id { get; set; }
    public long AgvId { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:8090";
    public string ClientCode { get; set; } = "CNC";
    public string CallbackHost { get; set; } = "0.0.0.0";
    public int CallbackPort { get; set; } = 9080;
    public int RequestTimeoutMs { get; set; } = 10000;
    public int MaxRetries { get; set; } = 3;
    public int PollIntervalMs { get; set; } = 3000;
}

/// <summary>
/// 进程内可变 RCS 连接配置。启动时由 appsettings 初始化，再被库表覆盖；
/// 页面保存后热更新出站相关字段。回调 Host/Port 变更需重启才重绑 Kestrel。
/// </summary>
public interface IRcsRuntimeConfig
{
    string BaseUrl { get; }
    string ClientCode { get; }
    string Version { get; }
    string TokenCode { get; }
    int RequestTimeoutMs { get; }
    int MaxRetries { get; }
    string CallbackHost { get; }
    int CallbackPort { get; }
    int PollIntervalMs { get; }

    /// <summary>启动时绑定的回调 Host（用于判断是否需重启）。</summary>
    string BootCallbackHost { get; }
    /// <summary>启动时绑定的回调 Port。</summary>
    int BootCallbackPort { get; }

    void Apply(RcsConnectionConfig config);
    /// <summary>将当前回调 Host/Port 记为启动快照（bootstrap 加载后、Kestrel 绑定前调用）。</summary>
    void CaptureBootCallback();
    RcsConnectionConfig Snapshot();
}
