using CncLoader.UI.ViewModels;

namespace CncLoader.UI.Navigation;

public sealed class NavigationService : INavigationService
{
    private readonly IReadOnlyDictionary<string, PageViewModelBase> _pages;

    public NavigationService(IEnumerable<PageViewModelBase> pages)
    {
        _pages = pages.ToDictionary(p => p.Key, p => p);
    }

    public PageViewModelBase? Current { get; private set; }

    public event EventHandler<PageViewModelBase>? Navigated;

    public void NavigateTo(string key)
    {
        if (!_pages.TryGetValue(key, out var page) || ReferenceEquals(page, Current))
            return;
        Current = page;
        Navigated?.Invoke(this, page);
    }
}
