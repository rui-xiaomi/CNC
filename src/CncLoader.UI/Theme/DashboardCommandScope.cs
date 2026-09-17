using System.Windows.Controls;

namespace CncLoader.UI.Theme;

/// <summary>看板命令宿主：工位/产线流模板经 AncestorType 绑定，不依赖 ItemsControl 层数。</summary>
public sealed class DashboardCommandScope : ContentControl
{
    public DashboardCommandScope()
    {
        IsTabStop = false;
        Focusable = false;
    }
}
