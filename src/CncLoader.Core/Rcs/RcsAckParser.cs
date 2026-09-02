using System.Text.Json;

namespace CncLoader.Core.Rcs;

/// <summary>
/// 解析 RCS 出站应答。文档事例 Success 为字符串 "true"；现场还可能是数字 1、缺字段只回 Message「成功」。
/// </summary>
public static class RcsAckParser
{
    /// <summary>出站 HTTP 2xx 即链路可达；业务 ACK 另判，不挡连通测试。</summary>
    public static bool IsHttpReachable(RcsResult r) => r.Ok;

    public static (bool success, string? message) Parse(string? respBody)
    {
        if (string.IsNullOrWhiteSpace(respBody)) return (false, null);
        try
        {
            using var doc = JsonDocument.Parse(respBody);
            var root = doc.RootElement;
            var message = ReadMessage(root);
            var success = ReadTruthy(root, "Success") ?? ReadTruthy(root, "success");
            if (success is null)
            {
                var code = ReadCode(root);
                if (code is 0 or 200) success = true;
                else if (code is not null) success = false;
            }

            if (success is null && IsSuccessMessage(message))
                success = true;

            return (success ?? false, message);
        }
        catch
        {
            return (false, null);
        }
    }

    private static string? ReadMessage(JsonElement root)
    {
        if (root.TryGetProperty("Message", out var m) && m.ValueKind == JsonValueKind.String)
            return m.GetString();
        if (root.TryGetProperty("message", out var m2) && m2.ValueKind == JsonValueKind.String)
            return m2.GetString();
        return null;
    }

    private static bool? ReadTruthy(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => ReadNumberFlag(v),
            JsonValueKind.String => ReadStringFlag(v.GetString()),
            _ => null
        };
    }

    private static int? ReadCode(JsonElement root)
    {
        foreach (var name in new[] { "Code", "code", "statusCode" })
        {
            if (!root.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String
                && int.TryParse(v.GetString()?.Trim(), out var ns))
                return ns;
        }
        return null;
    }

    private static bool? ReadNumberFlag(JsonElement v)
    {
        if (!v.TryGetInt32(out var n)) return null;
        return n switch
        {
            0 => false,
            1 => true,
            200 => true,
            _ => null
        };
    }

    private static bool? ReadStringFlag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (bool.TryParse(s, out var b)) return b;
        if (s is "1" or "200") return true;
        if (s is "0") return false;
        if (s.Equals("ok", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (s is "成功" or "执行成功") return true;
        if (s is "失败") return false;
        return null;
    }

    private static bool IsSuccessMessage(string? message)
        => message is "成功" or "执行成功";
}
