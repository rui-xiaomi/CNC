using System.Text.Json.Serialization;

namespace CncLoader.Core.Rcs;

/// <summary>RCS 任务种类（本系统区分，回查任务表用）。</summary>
public enum RcsTaskKind
{
    /// <summary>搬运任务 transitTask（cell 级）。</summary>
    Transit,
    /// <summary>抓取任务 grabTask（station 级）。</summary>
    Grab,
    /// <summary>识别/盘点 identifyQR。</summary>
    Identify,
    /// <summary>空托盘回收（transitTask，本系统语义标记）。</summary>
    PalletReturn,
    /// <summary>换架（transitTask 任务对，本系统语义标记）。</summary>
    ChangeFrame
}

public static class RcsTaskKindNames
{
    /// <summary>落库 RCS_KIND 值。</summary>
    public static string ToDbKind(RcsTaskKind k) => k switch
    {
        RcsTaskKind.Transit => "transit",
        RcsTaskKind.Grab => "grab",
        RcsTaskKind.Identify => "identify",
        RcsTaskKind.PalletReturn => "pallet_return",
        RcsTaskKind.ChangeFrame => "change_frame",
        _ => "transit"
    };

    /// <summary>taskId 中的类型缩写。</summary>
    public static string ToAbbrev(RcsTaskKind k) => k switch
    {
        RcsTaskKind.Transit => "TR",
        RcsTaskKind.Grab => "GR",
        RcsTaskKind.Identify => "ID",
        RcsTaskKind.PalletReturn => "PR",
        RcsTaskKind.ChangeFrame => "CF",
        _ => "TR"
    };
}

/// <summary>本系统任务态（RCS 11 态映射到此 5+1 态，见开发文档 §4.5）。</summary>
public static class RcsTaskState
{
    public const string Created = "CREATED";
    public const string Dispatched = "DISPATCHED";
    public const string Executing = "EXECUTING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string Canceled = "CANCELED";
}

/// <summary>点位元素（position 数组项）。第一个=取，最后一个=放。</summary>
public sealed class RcsPosition
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    /// <summary>shelf/cell/station。</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "station";

    public RcsPosition() { }
    public RcsPosition(string code, string type) { Code = code; Type = type; }
}

/// <summary>容器（可选；本系统默认不填，托盘由 RCS 自选）。</summary>
public sealed class RcsContainer
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("containerCode")] public string? ContainerCode { get; set; }
}

/// <summary>抓取任务 param 数组项（srcNo/srcPos/dstNo/dstPos/data 二维码值）。</summary>
public sealed class GrabItem
{
    [JsonPropertyName("srcNo")] public int SrcNo { get; set; }
    [JsonPropertyName("srcPos")] public int SrcPos { get; set; }
    [JsonPropertyName("dstNo")] public int DstNo { get; set; }
    [JsonPropertyName("dstPos")] public int DstPos { get; set; }
    [JsonPropertyName("data")] public string Data { get; set; } = "";
}

/// <summary>出站请求公共字段（transitTask/excuteTask/cancelTask 共用）。</summary>
public abstract class RcsRequestBase
{
    [JsonPropertyName("reqTime")] public string ReqTime { get; set; } = "";
    [JsonPropertyName("clientCode")] public string ClientCode { get; set; } = "";
    [JsonPropertyName("tokenCode")] public string TokenCode { get; set; } = "0";
    [JsonPropertyName("taskId")] public string TaskId { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    /// <summary>特殊命令，如 redo（失败重做，同 taskId 重发）。</summary>
    [JsonPropertyName("commandType")] public string? CommandType { get; set; }
}

/// <summary>3.1 执行搬运任务 transitTask。</summary>
public sealed class TransitTaskRequest : RcsRequestBase
{
    /// <summary>任务类型 in/out/move；本系统固定 move（RCS 预留字段）。</summary>
    [JsonPropertyName("taskType")] public string TaskType { get; set; } = "move";
    [JsonPropertyName("priority")] public int Priority { get; set; } = 5;
    [JsonPropertyName("container")] public RcsContainer? Container { get; set; }
    [JsonPropertyName("position")] public List<RcsPosition> Position { get; set; } = new();
}

/// <summary>3.2 执行定制任务 excuteTask（grabTask / identifyQR）。</summary>
public sealed class ExcuteTaskRequest : RcsRequestBase
{
    /// <summary>抓取：JSON 字符串（GrabItem 数组）；识别：形如 "101,3"（起始孔位,数量）。</summary>
    [JsonPropertyName("param")] public string Param { get; set; } = "";
    [JsonPropertyName("priority")] public int Priority { get; set; } = 5;
    /// <summary>grabTask / identifyQR。</summary>
    [JsonPropertyName("taskType")] public string TaskType { get; set; } = "grabTask";
    [JsonPropertyName("position")] public List<RcsPosition> Position { get; set; } = new();
}

/// <summary>3.3 取消任务 cancelTask（仅公共字段）。</summary>
public sealed class CancelTaskRequest : RcsRequestBase
{
}

/// <summary>3.4 查询任务 queryTask（无公共字段，仅条件 + 分页）。</summary>
public sealed class QueryTaskRequest
{
    [JsonPropertyName("condition")] public QueryCondition Condition { get; set; } = new();
    [JsonPropertyName("pageIndex")] public int PageIndex { get; set; } = 1;
    [JsonPropertyName("pageSize")] public int PageSize { get; set; } = 100;
}

public sealed class QueryCondition
{
    /// <summary>AND / OR。</summary>
    [JsonPropertyName("relation")] public string Relation { get; set; } = "AND";
    [JsonPropertyName("conditions")] public List<QueryConditionItem> Conditions { get; set; } = new();
}

public sealed class QueryConditionItem
{
    /// <summary>字段名（首字母大写）。</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    /// <summary>GT/GTE/LT/LTE/EQ/LIKE/NE/IN。</summary>
    [JsonPropertyName("operator")] public string Operator { get; set; } = "EQ";
    /// <summary>None/Asc/Desc。</summary>
    [JsonPropertyName("order")] public string Order { get; set; } = "None";
}

/// <summary>RCS 应答外层 { Success, Message, Data }（Success 可能是布尔或字符串"true"）。</summary>
public sealed class RcsAck
{
    [JsonPropertyName("Success")] public object? Success { get; set; }
    [JsonPropertyName("Message")] public string? Message { get; set; }
    [JsonPropertyName("Data")] public object? Data { get; set; }
}

/// <summary>一次 RCS 调用的结果（含请求报文原文，供落库与展示）。</summary>
public sealed record RcsResult(
    bool Ok,
    int HttpStatus,
    bool Success,
    string? Message,
    string RequestBody,
    string? RawResponse,
    string? Error,
    int ElapsedMs)
{
    public static RcsResult Fail(string requestBody, string error, int httpStatus = 0, int elapsedMs = 0)
        => new(false, httpStatus, false, null, requestBody, null, error, elapsedMs);
}
