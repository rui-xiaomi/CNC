using System.Diagnostics;
using System.Net.Http;
using System.Text;
using CncLoader.Core.Rcs;

namespace CncLoader.Communication.Rcs;

/// <summary>本机回调环回 POST。低频探针，每次新建短超时 HttpClient。</summary>
internal static class RcsLocalCallbackProber
{
    internal const string ProbeBody =
        """{"taskId":"","data":{"system":{"error_code":0,"msg":"callback-probe"}}}""";

    public static async Task<RcsLocalCallbackProbeResult> PostPushAsync(
        string host, int port, CancellationToken ct = default)
    {
        var url = $"http://{host}:{port}{RcsCallbackInterfaces.PushTaskStatusPath}";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var content = new StringContent(ProbeBody, Encoding.UTF8, "application/json");
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await http.PostAsync(url, content, ct).ConfigureAwait(false);
            sw.Stop();
            var ack = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
                return RcsLocalCallbackProbeResult.Success(status, (int)sw.ElapsedMilliseconds, ack);
            if (status == StatusCodes.Status403Forbidden)
                return RcsLocalCallbackProbeResult.Forbidden(status, (int)sw.ElapsedMilliseconds, ack);
            return RcsLocalCallbackProbeResult.UnexpectedStatus(status, (int)sw.ElapsedMilliseconds, ack);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return RcsLocalCallbackProbeResult.Unreachable("超时", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return RcsLocalCallbackProbeResult.Unreachable(ex.Message, (int)sw.ElapsedMilliseconds);
        }
    }

    private static class StatusCodes
    {
        public const int Status403Forbidden = 403;
    }
}
