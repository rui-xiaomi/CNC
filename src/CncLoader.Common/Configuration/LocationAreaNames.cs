namespace CncLoader.Common.Configuration;

/// <summary>
/// LOCATION_MAP AREA 角色英文码（库内 / RCS 用；UI 中文仅经 LocationDisplayLabels 转换）。
/// </summary>
public static class LocationAreaNames
{
    public const string LoadArea = "LOAD_AREA";
    public const string UnloadArea = "UNLOAD_AREA";
    public const string FullBuffer = "FULL_BUFFER";
    public const string EmptyBuffer = "EMPTY_BUFFER";
    public const string PalletReturn = "PALLET_RETURN";

    public static bool IsConfiguredAreaRole(string? locName) =>
        locName is LoadArea or UnloadArea or FullBuffer or EmptyBuffer or PalletReturn;

    public static bool IsChangeFrameBuffer(string? locName) =>
        locName is EmptyBuffer or FullBuffer;
}
