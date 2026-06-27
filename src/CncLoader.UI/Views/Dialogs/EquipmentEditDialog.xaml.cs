using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;

namespace CncLoader.UI.Views.Dialogs;

public partial class EquipmentEditDialog : Window
{
    /// <summary>编辑模式时携带的机台 Id（新增模式为 0）。</summary>
    public long EditId { get; private set; }

    /// <summary>是否为编辑模式。</summary>
    public bool IsEdit { get; private set; }

    public EquipmentCreateModel? CreateResult { get; private set; }
    public EquipmentEditModel? EditResult { get; private set; }

    /// <summary>新增机台：传入工序/PLC 选项与建议编号。</summary>
    public EquipmentEditDialog(IReadOnlyList<NamedOption> crafts, IReadOnlyList<NamedOption> plcs,
        string suggestedNo, long? preselectCraftId) : this(crafts, plcs, suggestedNo, preselectCraftId, null) { }

    /// <summary>编辑机台：传入已有 EquipmentEditModel 预填。</summary>
    public EquipmentEditDialog(IReadOnlyList<NamedOption> crafts, IReadOnlyList<NamedOption> plcs,
        EquipmentEditModel edit) : this(crafts, plcs, edit.No, edit.CraftworkId, edit) { }

    private EquipmentEditDialog(IReadOnlyList<NamedOption> crafts, IReadOnlyList<NamedOption> plcs,
        string suggestedNo, long? preselectCraftId, EquipmentEditModel? edit)
    {
        InitializeComponent();

        CraftCombo.ItemsSource = crafts;
        PlcCombo.ItemsSource = BuildPlcOptions(plcs);

        if (edit is null)
        {
            IsEdit = false;
            EditId = 0;
            TitleText.Text = "新增机台";
            SubtitleText.Text = "保存时自动创建 2 个加工位并绑定独立 PLC。";
            CraftCombo.SelectedItem = crafts.FirstOrDefault(c => c.Id == preselectCraftId) ?? crafts.FirstOrDefault();
            PlcCombo.SelectedItem = (PlcCombo.ItemsSource as List<NamedOption>)?.FirstOrDefault(p => p.Id != 0)
                ?? (PlcCombo.ItemsSource as List<NamedOption>)?[0];
            NoBox.Text = suggestedNo;
            NoBox.IsEnabled = true;
            CodeBox.Text = $"M-{suggestedNo}";
            NameBox.Text = "";
            TypeBox.Text = "检测";
        }
        else
        {
            IsEdit = true;
            EditId = edit.Id;
            TitleText.Text = "编辑机台";
            SubtitleText.Text = "机台编号为业务键，不可修改；其余字段可改。";
            CraftCombo.SelectedItem = crafts.FirstOrDefault(c => c.Id == edit.CraftworkId) ?? crafts.FirstOrDefault();
            PlcCombo.SelectedItem = (PlcCombo.ItemsSource as List<NamedOption>)?.FirstOrDefault(p => p.Id == edit.PlcId)
                ?? (PlcCombo.ItemsSource as List<NamedOption>)?[0];
            NoBox.Text = edit.No;
            NoBox.IsEnabled = false;
            CodeBox.Text = edit.Code;
            NameBox.Text = edit.Name;
            TypeBox.Text = string.IsNullOrEmpty(edit.Type) ? "检测" : edit.Type;
        }
    }

    private static List<NamedOption> BuildPlcOptions(IReadOnlyList<NamedOption> plcs)
    {
        var options = new List<NamedOption> { new NamedOption(0, "无") };
        options.AddRange(plcs);
        return options;
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
        var plcId = (PlcCombo.SelectedItem as NamedOption)?.Id ?? 0;
        var type = string.IsNullOrWhiteSpace(TypeBox.Text) ? "检测" : TypeBox.Text.Trim();

        if (IsEdit)
        {
            EditResult = new EquipmentEditModel
            {
                Id = EditId,
                CraftworkId = craft.Id,
                No = NoBox.Text.Trim(),
                Name = NameBox.Text.Trim(),
                Code = CodeBox.Text.Trim(),
                Type = type,
                PlcId = plcId
            };
        }
        else
        {
            CreateResult = new EquipmentCreateModel
            {
                CraftworkId = craft.Id,
                No = NoBox.Text.Trim(),
                Name = NameBox.Text.Trim(),
                Code = CodeBox.Text.Trim(),
                Type = type,
                PlcId = plcId
            };
        }
        DialogResult = true;
    }
}
