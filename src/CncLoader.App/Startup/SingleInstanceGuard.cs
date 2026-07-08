using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace CncLoader.App.Startup;

/// <summary>全局 Mutex 单实例守卫：重复启动时激活已有窗口并退出。</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Global\CncLoader.App.SingleInstance";
    private const int SwRestore = 9;

    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    /// <summary>尝试取得单实例锁；若已有实例在运行则激活其主窗口并返回 null。</summary>
    public static SingleInstanceGuard? TryAcquire()
    {
        var mutex = new Mutex(true, MutexName, out var ownsMutex);
        if (ownsMutex) return new SingleInstanceGuard(mutex, true);

        ActivateExistingMainWindow();
        mutex.Dispose();
        return null;
    }

    private static void ActivateExistingMainWindow()
    {
        var current = Process.GetCurrentProcess();
        foreach (var proc in Process.GetProcessesByName(current.ProcessName))
        {
            if (proc.Id == current.Id) continue;
            try
            {
                var handle = proc.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;
                ShowWindow(handle, SwRestore);
                SetForegroundWindow(handle);
                return;
            }
            catch
            {
                // 进程可能在枚举期间退出，忽略
            }
        }
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* 非本线程持有 */ }
        }
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
