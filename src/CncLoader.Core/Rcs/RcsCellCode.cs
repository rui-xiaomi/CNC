namespace CncLoader.Core.Rcs;

/// <summary>
/// 料架搬运 cell：货架码 + 层编码（第 1 层=10、第 2 层=11…）+ 层内位 1 位。
/// 例：上料架 101 第 1 层左 → 101101；第 2 层左 → 101111；第 10 层右 → 101192。
/// </summary>
public static class RcsCellCode
{
    /// <summary>合成 cell。shelf 或坐标非法时返回 null，禁止调用方再拼假码。</summary>
    public static string? TryCompose(string? shelfCode, int layerNo, int posInLayer)
    {
        if (string.IsNullOrWhiteSpace(shelfCode) || layerNo < 1 || layerNo > 90
            || posInLayer < 1 || posInLayer > 9)
            return null;
        return $"{shelfCode.Trim()}{9 + layerNo}{posInLayer}";
    }

    /// <summary>从 cell 拆层/位。货架前缀必须与 shelf 一致；层编码为 10 起的两位。</summary>
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
        if (rest.Length != 3)
            return false;

        if (!int.TryParse(rest[..2], out var layerCode) || layerCode < 10)
            return false;
        if (!int.TryParse(rest[2..], out posInLayer) || posInLayer < 1 || posInLayer > 9)
            return false;
        layerNo = layerCode - 9;
        return layerNo >= 1;
    }

    /// <summary>列表/备注用：L2P1、L10P2。</summary>
    public static string FormatSlotLabel(int layerNo, int posInLayer) => $"L{layerNo}P{posInLayer}";
}
