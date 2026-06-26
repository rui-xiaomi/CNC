using CommunityToolkit.Mvvm.ComponentModel;

namespace CncLoader.UI.ViewModels;

/// <summary>所有 ViewModel 基类。</summary>
public abstract partial class ViewModelBase : ObservableObject
{
}

/// <summary>页面 ViewModel 基类：带导航键与页标题。</summary>
public abstract partial class PageViewModelBase : ViewModelBase
{
    /// <summary>导航键（与侧栏项对应，全局唯一）。</summary>
    public abstract string Key { get; }

    /// <summary>页标题（状态条显示）。</summary>
    public abstract string Title { get; }

    /// <summary>占位说明（Phase 1 各页骨架展示用）。</summary>
    public virtual string Description => "该模块将在后续开发阶段实现。";
}
