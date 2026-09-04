namespace CncLoader.Core.Abstractions;

/// <summary>
/// UI 线程调度接缝。ViewModel 一律经此切回 UI 线程改 <c>ObservableCollection</c> / 绑定属性，
/// 不直接摸 <c>Application.Current.Dispatcher</c>——那样 headless 单测里 <c>Current</c> 为 null，
/// ViewModel 只能靠散落的 null 判断做兜底。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>已在 UI 线程则直接执行，否则同步切过去执行完再返回。</summary>
    void Invoke(Action action);

    /// <summary>投递到 UI 线程后立即返回（不等待执行完成）。</summary>
    void Post(Action action);
}
