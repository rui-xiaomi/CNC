using System.Net.Http;

namespace CncLoader.Communication.Rcs;

/// <summary>
/// 出站 RCS <see cref="HttpClient"/> 的进程级拥有者，与 <c>IRcsTaskService</c> 同生命周期。
/// 单例复用连接池；<see cref="SocketsHttpHandler.PooledConnectionLifetime"/> 让 BaseUrl 热更新后能重新解析 DNS。
/// 由 Generic Host 停止时 Dispose，不把 <c>IRcsClient</c> 暴露进容器。
/// </summary>
internal sealed class RcsOutboundHttpClientOwner : IDisposable
{
    public HttpClient Client { get; }

    public RcsOutboundHttpClientOwner()
    {
        Client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        });
    }

    public void Dispose() => Client.Dispose();
}
