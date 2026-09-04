namespace CncLoader.Core.Abstractions;

/// <summary>
/// 用户通知接缝（对应 HandyControl Growl 与 MessageBox）。ViewModel 一律经此提示，
/// 不直接调静态 Growl / MessageBox——那样 headless 单测里没有视觉树会 NRE，
/// 且「操作员到底看到了什么」无法断言。
/// </summary>
public interface IUserNotificationService
{
    void Success(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message);

    /// <summary>阻断式二次确认（危险操作前）。确认返回 true，取消返回 false。</summary>
    bool Confirm(string message, string title);

    /// <summary>阻断式告警提示（单确认按钮），用于必须让操作员当场看到的现场告警。</summary>
    void Alert(string message, string title);
}
