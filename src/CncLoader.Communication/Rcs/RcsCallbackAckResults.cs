using Microsoft.AspNetCore.Http;

namespace CncLoader.Communication.Rcs;

/// <summary>
/// RCS 回调 Host 对外 ACK 的唯一 IResult 工厂（与端点映射共用，避免测试另建一套）。
/// </summary>
internal static class RcsCallbackAckResults
{
    internal const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>将处理器返回的 ACK JSON 映射为 HTTP 200 Content。</summary>
    internal static IResult FromAckBody(string ackBody)
        => Results.Content(ackBody, JsonContentType);
}
