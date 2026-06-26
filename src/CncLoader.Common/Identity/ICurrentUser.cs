namespace CncLoader.Common.Identity;

/// <summary>
/// 当前操作人（轻量，不建用户表）。供各表 AUTHOR 字段与操作流水记录使用。
/// </summary>
public interface ICurrentUser
{
    /// <summary>操作人名：配置显式指定优先，否则取本机登录用户名。</summary>
    string Name { get; }
}
