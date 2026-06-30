using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CncLoader.Core.Plc;

namespace CncLoader.UI.Views.Dialogs;

public partial class PlcEditDialog : Window
{
    private static readonly string[] Protocols = { "ModbusTCP", "ModbusRTU", "FINS" };

    public PlcEditModel? Result { get; private set; }

    /// <summary>新增 PLC：传入建议 PlcId 与全部机台选项。</summary>
    public PlcEditDialog(long suggestedPlcId, IReadOnlyList<EquipmentOption> equipments)
        : this(null, suggestedPlcId, equipments, null) { }

    /// <summary>编辑 PLC：传入已有 PlcEditModel、全部机台选项、当前绑定机台 Id（null=未绑定）。</summary>
    public PlcEditDialog(PlcEditModel? edit, long suggestedPlcId,
        IReadOnlyList<EquipmentOption> equipments, long? boundEquipmentId)
    {
        InitializeComponent();

        ProtocolCombo.ItemsSource = Protocols;
        ProtocolCombo.SelectionChanged += OnProtocolChanged;

        // 机台下拉：首项"未绑定"(Id=0) + 全部启用机台；选中当前绑定项
        var options = new List<EquipmentOption> { new EquipmentOption(0, "未绑定", 0) };
        options.AddRange(equipments);
        EquipmentCombo.ItemsSource = options;
        var selectedId = boundEquipmentId is > 0 ? boundEquipmentId.Value : 0;
        EquipmentCombo.SelectedItem = options.FirstOrDefault(o => o.Id == selectedId) ?? options[0];

        if (edit is null)
        {
            TitleText.Text = "新增 PLC";
            SubtitleText.Text = "一机一 PLC，IP 各异；PlcId 为业务键，被机台/点位引用。";
            PlcIdBox.Text = suggestedPlcId.ToString();
            PlcIdBox.IsEnabled = true;
            PlcIdHint.Text = $"建议 {suggestedPlcId}";
            NameBox.Text = "";
            IpBox.Text = "192.168.1.";
            PortBox.Text = "502";
            ProtocolCombo.SelectedItem = "ModbusTCP";
        }
        else
        {
            TitleText.Text = "编辑 PLC";
            SubtitleText.Text = "PlcId 为业务键，不可修改；仅可改名称/IP/端口/协议/对应机台。";
            PlcIdBox.Text = edit.PlcId.ToString();
            PlcIdBox.IsEnabled = false;
            PlcIdHint.Text = "不可修改";
            NameBox.Text = edit.Name;
            IpBox.Text = edit.Ip;
            PortBox.Text = edit.Port.ToString();
            ProtocolCombo.SelectedItem = string.IsNullOrEmpty(edit.Protocol) ? "ModbusTCP" : edit.Protocol;
        }
    }

    /// <summary>切到 FINS 且端口仍为 Modbus 默认 502 时，建议改为 9600（仅轻量联动，不强制覆盖用户值）。</summary>
    private void OnProtocolChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProtocolCombo.SelectedItem is not string protocol) return;
        var isFins = protocol.Contains("FINS", System.StringComparison.OrdinalIgnoreCase);
        if (isFins && PortBox.Text.Trim() == "502")
            PortBox.Text = "9600";
        else if (!isFins && PortBox.Text.Trim() == "9600")
            PortBox.Text = "502";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(PlcIdBox.Text, out var plcId) || plcId <= 0)
        {
            HandyControl.Controls.Growl.Warning("PLC 编号需为正整数。");
            return;
        }
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            HandyControl.Controls.Growl.Warning("PLC 名称必填。");
            return;
        }
        if (string.IsNullOrWhiteSpace(IpBox.Text))
        {
            HandyControl.Controls.Growl.Warning("IP 地址必填。");
            return;
        }
        if (!int.TryParse(PortBox.Text, out var port) || port <= 0 || port > 65535)
        {
            HandyControl.Controls.Growl.Warning("端口需为 1..65535。");
            return;
        }
        var protocol = ProtocolCombo.SelectedItem as string ?? "ModbusTCP";
        var boundEq = EquipmentCombo.SelectedItem as EquipmentOption;
        var boundId = boundEq is null || boundEq.Id == 0 ? (long?)null : boundEq.Id;

        Result = new PlcEditModel(plcId, NameBox.Text.Trim(), IpBox.Text.Trim(), port, protocol)
        {
            BoundEquipmentId = boundId
        };
        DialogResult = true;
    }
}
