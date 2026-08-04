using CncLoader.Core.Abstractions;

namespace CncLoader.UI.Services;

/// <summary>生产通知：转发至 HandyControl 静态 Growl（行为与直调一致）。</summary>
public sealed class HandyControlUserNotificationService : IUserNotificationService
{
    public void Success(string message) => HandyControl.Controls.Growl.Success(message);
    public void Info(string message) => HandyControl.Controls.Growl.Info(message);
    public void Warning(string message) => HandyControl.Controls.Growl.Warning(message);
    public void Error(string message) => HandyControl.Controls.Growl.Error(message);
}
