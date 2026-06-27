using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;

namespace CncLoader.UI.Views.Dialogs;

public partial class EquipmentEditDialog : Window
{
    public EquipmentCreateModel? Result { get; private set; }

    public EquipmentEditDialog(IReadOnlyList<NamedOption> crafts, IReadOnlyList<NamedOption> plcs,
        string suggestedNo, long? preselectCraftId)
    {
        InitializeComponent();
        CraftCombo.ItemsSource = crafts;
        CraftCombo.SelectedItem = crafts.FirstOrDefault(c => c.Id == preselectCraftId) ?? crafts.FirstOrDefault();
        PlcCombo.ItemsSource = plcs;
        PlcCombo.SelectedItem = plcs.FirstOrDefault();
        NoBox.Text = suggestedNo;
        CodeBox.Text = $"M-{suggestedNo}";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (CraftCombo.SelectedItem is not NamedOption craft)
        {
            HandyControl.Controls.Growl.Warning("请选择所属工序。");
            return;
        }
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(NoBox.Text))
        {
            HandyControl.Controls.Growl.Warning("机台编号与名称为必填项。");
            return;
        }
        Result = new EquipmentCreateModel
        {
            CraftworkId = craft.Id,
            No = NoBox.Text.Trim(),
            Name = NameBox.Text.Trim(),
            Code = CodeBox.Text.Trim(),
            Type = string.IsNullOrWhiteSpace(TypeBox.Text) ? "检测" : TypeBox.Text.Trim(),
            PlcId = (PlcCombo.SelectedItem as NamedOption)?.Id ?? 0
        };
        DialogResult = true;
    }
}
