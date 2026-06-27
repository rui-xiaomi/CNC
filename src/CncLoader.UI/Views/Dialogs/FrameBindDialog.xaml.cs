using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CncLoader.Core.Abstractions;

namespace CncLoader.UI.Views.Dialogs;

public partial class FrameBindDialog : Window
{
    public long? UploadFrameId { get; private set; }
    public long? DownloadFrameId { get; private set; }

    public FrameBindDialog(string equipmentDisplay, IReadOnlyList<NamedOption> frames,
        long? currentUploadId, long? currentDownloadId)
    {
        InitializeComponent();
        HeaderText.Text = $"配置关联料架 · {equipmentDisplay}";

        // 「无」选项 + 料架列表
        var options = new List<NamedOption> { new(0, "无") };
        options.AddRange(frames);
        UploadCombo.ItemsSource = options;
        DownloadCombo.ItemsSource = options.ToList();
        UploadCombo.SelectedItem = options.FirstOrDefault(o => o.Id == (currentUploadId ?? 0)) ?? options[0];
        DownloadCombo.SelectedItem = options.FirstOrDefault(o => o.Id == (currentDownloadId ?? 0)) ?? options[0];
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var up = (UploadCombo.SelectedItem as NamedOption)?.Id ?? 0;
        var down = (DownloadCombo.SelectedItem as NamedOption)?.Id ?? 0;
        UploadFrameId = up > 0 ? up : null;
        DownloadFrameId = down > 0 ? down : null;
        DialogResult = true;
    }
}
