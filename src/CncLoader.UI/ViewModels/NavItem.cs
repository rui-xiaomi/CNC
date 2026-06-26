namespace CncLoader.UI.ViewModels;

/// <summary>侧栏导航项（扁平、单层、非树形）。Group 用于分组标题。</summary>
public sealed record NavItem(string Key, string Label, string Group, string IconData);
