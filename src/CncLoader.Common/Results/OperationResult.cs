namespace CncLoader.Common.Results;

/// <summary>
/// 轻量操作结果：业务层错误不靠异常穿透到 UI，统一用此类型表达成功/失败。
/// </summary>
public class OperationResult
{
    public bool Success { get; protected init; }
    public string? Error { get; protected init; }

    public static OperationResult Ok() => new() { Success = true };
    public static OperationResult Fail(string error) => new() { Success = false, Error = error };

    public static OperationResult<T> Ok<T>(T value) => OperationResult<T>.Ok(value);
    public static OperationResult<T> Fail<T>(string error) => OperationResult<T>.Fail(error);
}

/// <summary>带返回值的操作结果。</summary>
public sealed class OperationResult<T> : OperationResult
{
    public T? Value { get; private init; }

    public static OperationResult<T> Ok(T value) => new() { Success = true, Value = value };
    public static new OperationResult<T> Fail(string error) => new() { Success = false, Error = error };
}
