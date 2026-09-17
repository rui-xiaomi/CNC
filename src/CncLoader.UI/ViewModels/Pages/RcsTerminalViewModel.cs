using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>调用终端：行缓冲与清空。写入经 UI 调度器。</summary>
public sealed partial class RcsTerminalViewModel : ObservableObject
{
    private readonly IRcsUiBridge _ui;

    internal RcsTerminalViewModel(IRcsUiBridge ui)
    {
        _ui = ui;
        TerminalLines = new ObservableCollection<string>();
    }

    public ObservableCollection<string> TerminalLines { get; }

    [RelayCommand]
    private void ClearTerminal() => _ui.Ui.Invoke(() => TerminalLines.Clear());

    public void Append(string line)
        => _ui.Ui.Invoke(() =>
        {
            TerminalLines.Add(line);
            while (TerminalLines.Count > 200) TerminalLines.RemoveAt(0);
        });
}
