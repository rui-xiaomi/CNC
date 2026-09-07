namespace CncLoader.Core.Rcs;

/// <summary>
/// 抓取孔位编码：层×100+层内位（L1P1=101），与 identifyQR / 手动抓取默认值一致。
/// </summary>
public static class RcsGrabHole
{
    public static int FromSlot(int layerNo, int posInLayer) => layerNo * 100 + posInLayer;

    public static bool TryFromPositionCell(string? cellCode, string? stationCode, out int hole)
    {
        hole = 0;
        if (!RcsCellCode.TryParse(cellCode, stationCode, out var layerNo, out var posInLayer))
            return false;
        hole = FromSlot(layerNo, posInLayer);
        return true;
    }

    public static bool TryStationNo(string? stationCode, out int stationNo)
    {
        stationNo = 0;
        return !string.IsNullOrWhiteSpace(stationCode)
               && int.TryParse(stationCode.Trim(), out stationNo)
               && stationNo > 0;
    }

    public static GrabItem? TryBuild(
        string srcStation, int srcLayerNo, int srcPosInLayer,
        string dstStation, int dstLayerNo, int dstPosInLayer,
        string? data)
    {
        if (!TryStationNo(srcStation, out var srcNo) || !TryStationNo(dstStation, out var dstNo))
            return null;
        if (srcLayerNo < 1 || srcPosInLayer < 1 || dstLayerNo < 1 || dstPosInLayer < 1)
            return null;

        return new GrabItem
        {
            SrcNo = srcNo,
            SrcPos = FromSlot(srcLayerNo, srcPosInLayer),
            DstNo = dstNo,
            DstPos = FromSlot(dstLayerNo, dstPosInLayer),
            Data = data ?? ""
        };
    }

    public static GrabItem? TryBuildFromCells(
        string srcStation, string? srcCell,
        string dstStation, string? dstCell,
        string? data)
    {
        if (!RcsCellCode.TryParse(srcCell, srcStation, out var srcLayer, out var srcPos))
            return null;
        if (!RcsCellCode.TryParse(dstCell, dstStation, out var dstLayer, out var dstPos))
            return null;
        return TryBuild(srcStation, srcLayer, srcPos, dstStation, dstLayer, dstPos, data);
    }
}
