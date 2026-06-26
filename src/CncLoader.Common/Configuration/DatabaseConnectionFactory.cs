using CncLoader.Common.Security;
using Microsoft.Extensions.Logging;

namespace CncLoader.Common.Configuration;

/// <summary>
/// 从 <see cref="DatabaseOptions"/> 构建 MySQL 连接串。口令在此处解密，绝不写入日志。
/// </summary>
public interface IDatabaseConnectionFactory
{
    string BuildConnectionString();
}

public sealed class DatabaseConnectionFactory : IDatabaseConnectionFactory
{
    private readonly DatabaseOptions _db;
    private readonly ISecretProtector _protector;
    private readonly ILogger<DatabaseConnectionFactory> _logger;

    public DatabaseConnectionFactory(
        Microsoft.Extensions.Options.IOptions<AppOptions> options,
        ISecretProtector protector,
        ILogger<DatabaseConnectionFactory> logger)
    {
        _db = options.Value.Database;
        _protector = protector;
        _logger = logger;
    }

    public string BuildConnectionString()
    {
        var password = ResolvePassword();
        // 注意：仅返回连接串，不在任何日志中输出口令。
        return $"Server={_db.Server};Port={_db.Port};Database={_db.Database};Uid={_db.User};Pwd={password};{_db.ExtraParameters}";
    }

    private string ResolvePassword()
    {
        if (string.IsNullOrEmpty(_db.Password))
            return string.Empty;

        if (_db.PasswordProtected)
            return _protector.Unprotect(_db.Password);

        // 开发期允许明文，但提示加密。
        _logger.LogWarning("数据库口令以明文配置（PasswordProtected=false），生产环境请改为 DPAPI 密文。");
        return _db.Password;
    }
}
