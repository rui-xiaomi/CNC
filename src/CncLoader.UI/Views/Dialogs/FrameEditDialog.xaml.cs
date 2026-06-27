using System.Windows;
using CncLoader.Core.Config;

namespace CncLoader.UI.Views.Dialogs;

public partial class FrameEditDialog : Window
{
    public FrameCreateModel? Result { get; private set; }

    public FrameEditDialog() => InitializeComponent();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(IdentifyBox.Text))
        {
            HandyControl.Controls.Growl.Warning("料架名称与识别码为必填项。");
            return;
        }
        if (!int.TryParse(LayerBox.Text, out var layers) || layers < 1 ||
            !int.TryParse(PerLayerBox.Text, out var perLayer) || perLayer < 1)
        {
            HandyControl.Controls.Growl.Warning("层数与每层槽数需为不小于 1 的整数。");
            return;
        }
        Result = new FrameCreateModel
        {
            Name = NameBox.Text.Trim(),
            Code = string.IsNullOrWhiteSpace(CodeBox.Text) ? IdentifyBox.Text.Trim() : CodeBox.Text.Trim(),
            IdentifyCode = IdentifyBox.Text.Trim(),
            LayerTotal = layers,
            SlotsPerLayer = perLayer
        };
        DialogResult = true;
    }
}
