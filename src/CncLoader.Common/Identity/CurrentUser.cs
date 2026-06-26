using CncLoader.Common.Configuration;
using Microsoft.Extensions.Options;

namespace CncLoader.Common.Identity;

/// <summary>配置显式操作人优先，否则回退本机登录用户名。</summary>
public sealed class CurrentUser : ICurrentUser
{
    public CurrentUser(IOptions<AppOptions> options)
    {
        var configured = options.Value.Operator.Name;
        Name = string.IsNullOrWhiteSpace(configured)
            ? (Environment.UserName ?? "operator")
            : configured.Trim();
    }

    public string Name { get; }
}
