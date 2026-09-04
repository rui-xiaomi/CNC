namespace CncLoader.Core.Rcs;

/// <summary>
/// 现场搬运 cell 编码：货架码 + 层号（不补零）+ 层内位 2 位。
/// 例：上料架 101 第 1 层左 → 101101；第 10 层右 → 1011002；内长宽工位 1 → 201101。
/// </summary>
public static class RcsCellCode
{
    /// <summary>合成 cell。shelf 或坐标非法时返回 null，禁止调用方再拼假码。</summary>
    public static string? TryCompose(string? shelfCode, int layerNo, int posInLayer)
    {
        if (string.IsNullOrWhiteSpace(shelfCode) || layerNo < 1 || posInLayer < 1 || posInLayer > 99)
            return null;
        return $"{shelfCode.Trim()}{layerNo}{posInLayer:D2}";
    }

    /// <summary>从 cell 拆层/位。货架前缀必须与 shelf 一致，层号不补零。</summary>
    public static bool TryParse(string? rcsCode, string? shelfCode, out int layerNo, out int posInLayer)
    {
        layerNo = 0;
        posInLayer = 0;
        if (string.IsNullOrWhiteSpace(rcsCode) || string.IsNullOrWhiteSpace(shelfCode))
            return false;

        var code = rcsCode.Trim();
        var shelf = shelfCode.Trim();
        if (shelf.Length == 0 || !code.StartsWith(shelf, StringComparison.Ordinal))
            return false;

        var rest = code[shelf.Length..];
        if (rest.Length < 3)
            return false;

        var posPart = rest[^2..];
        var layerPart = rest[..^2];
        if (!int.TryParse(layerPart, out layerNo) || layerNo < 1)
            return false;
        if (!int.TryParse(posPart, out posInLayer) || posInLayer < 1 || posInLayer > 99)
            return false;
        return true;
    }

    /// <summary>列表/备注用：L2P1、L10P2。</summary>
    public static string FormatSlotLabel(int layerNo, int posInLayer) => $"L{layerNo}P{posInLayer}";
}
