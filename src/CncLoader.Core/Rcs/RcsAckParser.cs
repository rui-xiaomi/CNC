using System.Text.Json;

namespace CncLoader.Core.Rcs;

/// <summary>
/// 解析 RCS 出站应答。文档事例 Success 为字符串 "true"；现场还可能是数字 1、缺字段只回 Message「成功」。
/// </summary>
public static class RcsAckParser
{
    /// <summary>出站 HTTP 2xx 即链路可达；业务 ACK 另判，不挡连通测试。</summary>
    public static bool IsHttpReachable(RcsResult r) => r.Ok;

    /// <summary>
    /// 从 ACK <c>Data</c> 取 RCS 任务号：优先 <c>task_id</c>/<c>taskId</c>，否则 <c>booking.id</c>。
    /// 不取 <c>request_id</c>。<c>Data</c> 可为对象或字符串化 JSON；<c>json_msg.success=false</c> 跳过。
    /// </summary>
    public static string? TryReadAssignedTaskId(string? respBody)
    {
        if (string.IsNullOrWhiteSpace(respBody)) return null;
        try
        {
            using var doc = JsonDocument.Parse(respBody);
            if (!TryGetData(doc.RootElement, out var data)) return null;
            return ReadAssignedIdFromData(data);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    private static bool TryGetData(JsonElement root, out JsonElement data)
    {
        if (root.TryGetProperty("Data", out data)) return true;
        if (root.TryGetProperty("data", out data)) return true;
        data = default;
        return false;
    }

    private static string? ReadAssignedIdFromData(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.String)
        {
            var raw = data.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                using var inner = JsonDocument.Parse(raw);
                return FindAssignedId(inner.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return data.ValueKind == JsonValueKind.Object ? FindAssignedId(data) : null;
    }

    private static string? FindAssignedId(JsonElement root)
    {
        if (root.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in values.EnumerateArray())
            {
                if (!item.TryGetProperty("json_msg", out var msg)) continue;
                if (IsJsonMsgRejected(msg)) continue;
                var id = ReadTaskIdOrBooking(msg);
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }

            return null;
        }

        return ReadTaskIdOrBooking(root);
    }

    private static string? ReadTaskIdOrBooking(JsonElement msg)
    {
        var taskId = FindNamedString(msg, "task_id") ?? FindNamedString(msg, "taskId");
        if (!string.IsNullOrWhiteSpace(taskId)) return taskId.Trim();
        var booking = ReadNestedBookingId(msg);
        return string.IsNullOrWhiteSpace(booking) ? null : booking.Trim();
    }

    private static string? FindNamedString(JsonElement el, string name)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.String)
                {
                    var s = direct.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                foreach (var p in el.EnumerateObject())
                {
                    var inner = FindNamedString(p.Value, name);
                    if (inner is not null) return inner;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    var inner = FindNamedString(item, name);
                    if (inner is not null) return inner;
                }
                break;
        }
        return null;
    }

    private static bool IsJsonMsgRejected(JsonElement msg)
    {
        if (!msg.TryGetProperty("success", out var s)) return false;
        return s.ValueKind switch
        {
            JsonValueKind.False => true,
            JsonValueKind.String => bool.TryParse(s.GetString(), out var b) && !b,
            JsonValueKind.Number => s.TryGetInt32(out var n) && n == 0,
            _ => false
        };
    }

    private static string? ReadNestedBookingId(JsonElement msg)
    {
        if (msg.TryGetProperty("state", out var state)
            && state.TryGetProperty("booking", out var booking)
            && booking.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String)
            return id.GetString();

        if (msg.TryGetProperty("booking", out var booking2)
            && booking2.TryGetProperty("id", out var id2)
            && id2.ValueKind == JsonValueKind.String)
            return id2.GetString();

        return null;
    }
}
