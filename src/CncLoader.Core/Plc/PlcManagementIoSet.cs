namespace CncLoader.Core.Plc;

/// <summary>
/// PLC 管理页读/写面板的点位集合。
/// 映射表 <see cref="PlcPointRow.IsWrite"/> 只描述信号方向（轮询/调度仍按此过滤），
/// 不决定本页可见性：读面板展示全部点位，写面板也可对每个点位下发。
/// </summary>
public static class PlcManagementIoSet
{
    public static IReadOnlyList<PlcPointRow> ForManualIo(IEnumerable<PlcPointRow> mappedPoints)
        => mappedPoints.ToList();

    public static IReadOnlyList<WriteSignalOption> ToWriteOptions(
        IEnumerable<PlcPointRow> mappedPoints,
        string? equipmentName = null)
        => ForManualIo(mappedPoints)
            .Select(p => new WriteSignalOption(
                p.Id,
                FormatWriteDisplayName(equipmentName, p.PositionName, p.SignalLabel, p.RegisterAddress),
                p.RegisterAddress,
                p.PlcId,
                p.EquipmentId))
            .ToList();

    public static string FormatWriteDisplayName(
        string? equipmentName,
        string? positionName,
        string signalLabel,
        string registerAddress)
    {
        var hasEq = !string.IsNullOrEmpty(equipmentName);
        var hasPos = !string.IsNullOrEmpty(positionName);
        if (hasEq && hasPos)
            return $"{equipmentName} · {positionName} {signalLabel} ({registerAddress})";
        if (hasEq)
            return $"{equipmentName} · {signalLabel} ({registerAddress})";
        if (hasPos)
            return $"{positionName} {signalLabel} ({registerAddress})";
        return $"{signalLabel} ({registerAddress})";
    }
}
