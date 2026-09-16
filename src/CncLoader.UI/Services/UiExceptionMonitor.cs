using CncLoader.Core.Abstractions;

namespace CncLoader.UI.Services;

/// <summary><see cref="IUiExceptionMonitor"/> 的线程安全实现（单例）。</summary>
public sealed class UiExceptionMonitor : IUiExceptionMonitor
{
    private readonly object _gate = new();
    private int _count;
    private DateTime? _lastAt;
    private string? _lastMessage;

    public int Count { get { lock (_gate) return _count; } }
    public DateTime? LastAt { get { lock (_gate) return _lastAt; } }
    public string? LastMessage { get { lock (_gate) return _lastMessage; } }

    public event EventHandler? Changed;

    public void Record(Exception exception)
    {
        lock (_gate)
        {
            _count++;
            _lastAt = DateTime.Now;
            _lastMessage = $"{exception.GetType().Name}: {exception.Message}";
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
