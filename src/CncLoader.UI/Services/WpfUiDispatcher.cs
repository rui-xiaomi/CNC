using System.Windows;
using CncLoader.Core.Abstractions;

namespace CncLoader.UI.Services;

/// <summary>
/// 生产实现：转发至 WPF <see cref="System.Windows.Threading.Dispatcher"/>。
/// 无 <see cref="Application.Current"/>（headless 单测 / 设计器）时同步直跑，
/// 让 ViewModel 不必自己写「没有 Application 就同步执行」的兜底分支。
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        dispatcher.Invoke(action);
    }

    public void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }
        dispatcher.BeginInvoke(action);
    }
}
