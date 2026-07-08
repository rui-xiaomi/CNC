using System.Windows;
using CncLoader.Core.Config;

namespace CncLoader.UI.Views.Dialogs;

public partial class FrameEditDialog : Window
{
    private readonly long _editId; // 0=新增

    /// <summary>新增结果（新增模式）。</summary>
    public FrameCreateModel? Result { get; private set; }
    /// <summary>编辑结果（编辑模式）。</summary>
    public FrameEditModel? EditResult { get; private set; }

    public FrameEditDialog() => InitializeComponent();

    /// <summary>编辑模式：预填现值。</summary>
    public FrameEditDialog(FrameEditModel edit)
    {
        InitializeComponent();
        _editId = edit.Id;
        Title = "编辑料架";
        HeaderText.Text = "编辑料架";
        HintText.Text = "改层数/每层槽数会重建空槽位；料架有料/占用时需先清空再改。";
        NameBox.Text = edit.Name;
        CodeBox.Text = edit.Code;
        IdentifyBox.Text = edit.IdentifyCode;
        LayerBox.Text = edit.LayerTotal.ToString();
        PerLayerBox.Text = edit.SlotsPerLayer.ToString();
    }

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
        var code = string.IsNullOrWhiteSpace(CodeBox.Text) ? IdentifyBox.Text.Trim() : CodeBox.Text.Trim();
        if (_editId > 0)
        {
            EditResult = new FrameEditModel
            {
                Id = _editId, Name = NameBox.Text.Trim(), Code = code, IdentifyCode = IdentifyBox.Text.Trim(),
                LayerTotal = layers, SlotsPerLayer = perLayer
            };
        }
        else
        {
            Result = new FrameCreateModel
            {
                Name = NameBox.Text.Trim(), Code = code, IdentifyCode = IdentifyBox.Text.Trim(),
                LayerTotal = layers, SlotsPerLayer = perLayer
            };
        }
        DialogResult = true;
    }
}
