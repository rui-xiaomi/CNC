namespace CncLoader.Core.Tests.Rcs;

/// <summary>现场 grabTask ACK 原文（Data 为字符串，内含 task_id）。</summary>
internal static class RcsFieldAck
{
    public const string TaskId = "CNC_WMS_TASK_2_2026-09-11_0225134113";
    public const string RequestId = "6312e6e9-3bbb-46bf-a856-e3f331b3c3c4";

    /// <summary>用户贴出的现场回包（已压缩空白）。</summary>
    public static string GrabAck() =>
        """
        {"Success":true,"Message":"发送成功！","Data":"{\"conflicts\":null,\"response\":\"task_api_responses\",\"values\":[{\"json_msg\":{\"state\":{\"booking\":{\"id\":\"CNC_WMS_TASK_2_2026-09-11_0225134113\",\"priority\":{\"type\":\"binary\",\"value\":1},\"requester\":\"byd\",\"unix_millis_earliest_start_time\":0,\"unix_millis_request_time\":1789107913},\"category\":\"compose\",\"detail\":{\"category\":\"CNC_WMS_TASK_2_2026-09-11_0225134113\",\"phases\":[{\"activity\":{\"category\":\"sequence\",\"description\":{\"activities\":[{\"category\":\"go_to_place\",\"description\":{\"perform_actions\":[{\"LM101\":{\"actionDescription\":\"\",\"actionId\":\"bcd9953d-2e8c-4a96-9016-17e941efac77\",\"actionParameters\":[{\"key\":\"products\",\"value\":\"[{\\\"srcNo\\\":101,\\\"srcPos\\\":101,\\\"dstNo\\\":401,\\\"dstPos\\\":101,\\\"data\\\":\\\"QR-TEST-001\\\"}]\"}],\"actionType\":\"robotPickDrop\",\"blockingType\":\"HARD\"}}],\"place\":\"LM101\",\"task_id\":\"CNC_WMS_TASK_2_2026-09-11_0225134113\"}},{\"category\":\"go_to_place\",\"description\":{\"perform_actions\":[{\"LM201\":{\"actionDescription\":\"\",\"actionId\":\"5ae7bcda-7b2e-4a18-830b-16a9624e220f\",\"actionParameters\":[{\"key\":\"products\",\"value\":\"[{\\\"srcNo\\\":401,\\\"srcPos\\\":101,\\\"dstNo\\\":201,\\\"dstPos\\\":101,\\\"data\\\":\\\"QR-TEST-001\\\"}]\"}],\"actionType\":\"robotPickDrop\",\"blockingType\":\"HARD\"}}],\"place\":\"LM201\",\"task_id\":\"CNC_WMS_TASK_2_2026-09-11_0225134113\"}}]}}}]},\"dispatch\":{\"errors\":[],\"status\":\"queued\"},\"status\":\"queued\",\"unix_millis_start_time\":0},\"success\":true},\"request_id\":\"6312e6e9-3bbb-46bf-a856-e3f331b3c3c4\",\"type\":2}]}","newtoken":null}
        """;
}
