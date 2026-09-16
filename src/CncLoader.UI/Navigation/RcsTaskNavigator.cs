using Microsoft.Extensions.DependencyInjection;

namespace CncLoader.UI.Navigation;

/// <summary>经 <see cref="IServiceProvider"/> 取导航，避免看板 ↔ 导航容器循环构造。</summary>
public sealed class RcsTaskNavigator : IRcsTaskNavigator
{
    private readonly IServiceProvider _services;

    public RcsTaskNavigator(IServiceProvider services) => _services = services;

    public void OpenTask(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;
        _services.GetRequiredService<INavigationService>().NavigateTo("rcs", taskId.Trim());
    }
}
