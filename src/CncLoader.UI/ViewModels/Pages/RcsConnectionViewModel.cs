using CncLoader.Core.Rcs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>连接面板：保存/探测出站、本机回调探针。不持有 HttpClient。</summary>
public sealed partial class RcsConnectionViewModel : ObservableObject
{
    private readonly IRcsPageCoordinator _page;
    private bool _suppressVerifyInvalidation;

    internal RcsConnectionViewModel(IRcsPageCoordinator page) => _page = page;

    [ObservableProperty] private string _baseUrl = "";
    [ObservableProperty] private string _clientCode = "";
    [ObservableProperty] private string _callbackHost = "0.0.0.0";
    [ObservableProperty] private int _callbackPort = 9080;
    [ObservableProperty] private int _requestTimeoutMs = 10000;
    [ObservableProperty] private int _maxRetries = 3;
    [ObservableProperty] private int _pollIntervalMs = 3000;
    [ObservableProperty] private bool _isSavingConnection;
    [ObservableProperty] private bool _isTestingConnection;
    [ObservableProperty] private string _connectionHealthText = "未测试";
    [ObservableProperty] private string _connectionHealthBrushKey = "IdleBrush";
    [ObservableProperty] private bool _isTestingCallback;
    [ObservableProperty] private string _callbackHealthText = "未测试";
    [ObservableProperty] private string _callbackHealthBrushKey = "IdleBrush";
    [ObservableProperty] private string _effectiveBaseUrl = "";

    partial void OnBaseUrlChanged(string value)
    {
        if (_suppressVerifyInvalidation) return;
        _page.InvalidateConnectionVerification("BaseUrl 已修改");
    }

    partial void OnClientCodeChanged(string value)
    {
        if (_suppressVerifyInvalidation) return;
        _page.InvalidateConnectionVerification("ClientCode 已修改");
    }

    [RelayCommand]
    private Task SaveConnectionAsync() => SaveAsync();

    [RelayCommand]
    private Task TestConnectionAsync() => TestOutboundAsync();

    [RelayCommand]
    private Task TestCallbackAsync() => TestLocalCallbackAsync();

    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            _page.Notify.Warning("请填写 RCS 地址。");
            return;
        }
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _page.Notify.Warning("RCS 地址须为 http(s)://… 形式。");
            return;
        }
        if (string.IsNullOrWhiteSpace(ClientCode))
        {
            _page.Notify.Warning("请填写 clientCode。");
            return;
        }
        if (CallbackPort is < 1 or > 65535)
        {
            _page.Notify.Warning("回调端口无效。");
            return;
        }
        var callbackHost = string.IsNullOrWhiteSpace(CallbackHost) ? "0.0.0.0" : CallbackHost.Trim();
        if (!System.Net.IPAddress.TryParse(callbackHost, out var callbackIp))
        {
            _page.Notify.Warning("回调 Host 须为合法 IP（如 0.0.0.0 或 127.0.0.1）。");
            return;
        }
        callbackHost = callbackIp.Equals(System.Net.IPAddress.Any) || callbackIp.Equals(System.Net.IPAddress.IPv6Any)
            ? "0.0.0.0"
            : callbackIp.ToString();

        IsSavingConnection = true;
        try
        {
            var draft = new RcsConnectionConfig
            {
                Id = _page.ConnectionConfigId,
                AgvId = _page.AgvId > 0 ? _page.AgvId : 1,
                BaseUrl = BaseUrl.Trim(),
                ClientCode = ClientCode.Trim(),
                CallbackHost = callbackHost,
                CallbackPort = CallbackPort,
                RequestTimeoutMs = RequestTimeoutMs,
                MaxRetries = MaxRetries,
                PollIntervalMs = PollIntervalMs
            };
            var saved = await _page.ConnConfig.SaveAsync(draft, _page.User.Name);
            _page.ConnectionConfigId = saved.Id;
            _page.AgvId = saved.AgvId;
            _page.Runtime.Apply(saved);
            _page.LoadConnectionFromRuntime();
            if (!string.Equals(_page.VerifiedConnectionKey, _page.ConnectionKey(saved.BaseUrl, saved.ClientCode), StringComparison.Ordinal))
                _page.InvalidateConnectionVerification("连接配置已保存且出站目标变更");
            else
                _page.RefreshDispatchGateHint();

            var callbackChanged = !string.Equals(saved.CallbackHost, _page.Runtime.BootCallbackHost, StringComparison.OrdinalIgnoreCase)
                                  || saved.CallbackPort != _page.Runtime.BootCallbackPort;
            if (callbackChanged)
                _page.Notify.Warning("已保存。回调 Host/Port 已变更，需重启客户端后生效。");
            else
                _page.Notify.Success("RCS 连接配置已保存（出站立即生效）。");
            _page.Append($"> 已保存连接配置 {saved.BaseUrl} client={saved.ClientCode}");
        }
        catch (Exception ex)
        {
            _page.Notify.Error($"保存失败：{ex.Message}");
        }
        finally { IsSavingConnection = false; }
    }

    public Task TestOutboundAsync() => TestConnectionCoreAsync();

    public Task TestLocalCallbackAsync() => TestCallbackCoreAsync();

    private async Task TestConnectionCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl)
            || !Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ConnectionHealthText = "地址无效";
            ConnectionHealthBrushKey = "AlarmBrush";
            _page.Notify.Warning("请先填写有效的 RCS 地址。");
            return;
        }

        IsTestingConnection = true;
        ConnectionHealthText = "测试中…";
        ConnectionHealthBrushKey = "WarnBrush";
        try
        {
            var probe = new RcsConnectionConfig
            {
                Id = _page.ConnectionConfigId,
                AgvId = _page.AgvId,
                BaseUrl = BaseUrl.Trim(),
                ClientCode = string.IsNullOrWhiteSpace(ClientCode) ? "CNC" : ClientCode.Trim(),
                CallbackHost = string.IsNullOrWhiteSpace(CallbackHost) ? "0.0.0.0" : CallbackHost.Trim(),
                CallbackPort = CallbackPort,
                RequestTimeoutMs = RequestTimeoutMs,
                MaxRetries = MaxRetries,
                PollIntervalMs = PollIntervalMs
            };

            _page.Append($"> 测试连接 queryTask → {probe.BaseUrl}");
            var r = await _page.Rcs.ProbeQueryAsync(probe, new QueryTaskRequest { PageIndex = 1, PageSize = 1 });
            if (RcsAckParser.IsHttpReachable(r))
            {
                ConnectionHealthText = $"连通 {r.ElapsedMs}ms";
                ConnectionHealthBrushKey = "OkBrush";
                _page.VerifiedConnectionKey = _page.ConnectionKey(BaseUrl, ClientCode);
                if (r.Success)
                    _page.Append($"< 连通 OK {r.ElapsedMs}ms");
                else
                    _page.Append($"< 连通 OK HTTP{r.HttpStatus} {r.ElapsedMs}ms（业务ACK：{r.Message ?? "无 Success"}）");
                _page.Notify.Success($"RCS 连通成功 {r.ElapsedMs}ms");
                _page.RefreshDispatchGateHint();
            }
            else
            {
                var detail = r.Error ?? r.Message ?? "失败";
                ConnectionHealthText = "不通";
                ConnectionHealthBrushKey = "AlarmBrush";
                _page.VerifiedConnectionKey = null;
                _page.Append($"< 连通失败 HTTP{r.HttpStatus} {detail}");
                _page.Notify.Warning($"RCS 连通失败：{detail}");
                _page.RefreshDispatchGateHint();
            }
        }
        catch (Exception ex)
        {
            ConnectionHealthText = "不通";
            ConnectionHealthBrushKey = "AlarmBrush";
            _page.VerifiedConnectionKey = null;
            _page.Append($"< 连通异常 {ex.Message}");
            _page.Notify.Error($"测试异常：{ex.Message}");
            _page.RefreshDispatchGateHint();
        }
        finally
        {
            IsTestingConnection = false;
            await _page.RefreshMessagesAsync();
        }
    }

    private async Task TestCallbackCoreAsync()
    {
        IsTestingCallback = true;
        CallbackHealthText = "测试中…";
        CallbackHealthBrushKey = "WarnBrush";
        try
        {
            var host = CallbackLoopbackHost.Resolve(_page.CallbackListener.BoundHost);
            var url = $"http://{host}:{_page.CallbackListener.BoundPort}{RcsCallbackInterfaces.PushTaskStatusPath}";
            _page.Append($"> 测试本机监听 POST {url}");

            var r = await _page.CallbackListener.ProbePushEndpointAsync();
            switch (r.FailureKind)
            {
                case RcsLocalCallbackProbeFailureKind.None:
                    CallbackHealthText = $"本机可达 {r.ElapsedMs}ms";
                    CallbackHealthBrushKey = "OkBrush";
                    _page.Append($"< 本机监听 OK HTTP{r.HttpStatus} {r.ElapsedMs}ms（仅证明本机 Kestrel；不代表 RCS→工控机网络已通） {r.AckBody}");
                    _page.Notify.Success(
                        $"本机监听可达 {r.ElapsedMs}ms（不代表 RCS 服务器回调网络已打通）");
                    break;
                case RcsLocalCallbackProbeFailureKind.NotListening:
                    var err = r.Error ?? "回调宿主未启动";
                    CallbackHealthText = "未监听";
                    CallbackHealthBrushKey = "AlarmBrush";
                    _page.Append($"< 回调未监听：{err}");
                    _page.Notify.Warning($"回调未监听：{err}");
                    break;
                case RcsLocalCallbackProbeFailureKind.Forbidden:
                    CallbackHealthText = "本机被拒";
                    CallbackHealthBrushKey = "AlarmBrush";
                    _page.Append($"< 本机监听被白名单拒绝 HTTP{r.HttpStatus} {r.AckBody}");
                    _page.Notify.Warning("本机监听被白名单拒绝（127.0.0.1 应已自动放行，请重启客户端后再测）");
                    break;
                default:
                    CallbackHealthText = "本机不通";
                    CallbackHealthBrushKey = "AlarmBrush";
                    if (r.HttpStatus > 0)
                    {
                        _page.Append($"< 本机监听失败 HTTP{r.HttpStatus} {r.AckBody}");
                        _page.Notify.Warning($"本机监听不通：HTTP{r.HttpStatus}");
                    }
                    else
                    {
                        _page.Append($"< 本机监听异常 {r.Error}");
                        _page.Notify.Error($"本机监听测试异常：{r.Error}");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            CallbackHealthText = "本机不通";
            CallbackHealthBrushKey = "AlarmBrush";
            _page.Append($"< 本机监听异常 {ex.Message}");
            _page.Notify.Error($"本机监听测试异常：{ex.Message}");
        }
        finally
        {
            IsTestingCallback = false;
            await _page.RefreshMessagesAsync();
        }
    }

    internal void ApplyRuntimeSnapshot(RcsConnectionConfig snap)
    {
        _suppressVerifyInvalidation = true;
        try
        {
            BaseUrl = snap.BaseUrl;
            ClientCode = snap.ClientCode;
            CallbackHost = snap.CallbackHost;
            CallbackPort = snap.CallbackPort;
            RequestTimeoutMs = snap.RequestTimeoutMs;
            MaxRetries = snap.MaxRetries;
            PollIntervalMs = snap.PollIntervalMs;
        }
        finally { _suppressVerifyInvalidation = false; }
        EffectiveBaseUrl = snap.BaseUrl;
    }

    internal void RefreshListenHint(IRcsCallbackListener listener)
    {
        if (listener.IsListening)
        {
            CallbackHealthText = $"本机监听 {listener.BoundHost}:{listener.BoundPort}";
            CallbackHealthBrushKey = "OkBrush";
        }
        else
        {
            var err = listener.ListenError;
            CallbackHealthText = string.IsNullOrWhiteSpace(err) ? "未监听" : "启动失败";
            CallbackHealthBrushKey = "AlarmBrush";
        }
    }
}
