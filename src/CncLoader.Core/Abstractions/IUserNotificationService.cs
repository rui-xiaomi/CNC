namespace CncLoader.Core.Abstractions;

/// <summary>
/// 用户通知接缝（对应 HandyControl Growl）。供 ViewModel 可测；生产实现仍调 Growl。
/// </summary>
public interface IUserNotificationService
{
    void Success(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message);
}
