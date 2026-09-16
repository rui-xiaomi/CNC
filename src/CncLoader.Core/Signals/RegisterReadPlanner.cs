namespace CncLoader.Core.Signals;

/// <summary>一次批量读：从 <see cref="Address"/> 起连续读 <see cref="Length"/> 个字，各点位值取返回数组的 Index 位。</summary>
public sealed record RegisterReadBlock(
    string Address,
    int Length,
    IReadOnlyList<(PlcPointDefinition Point, int Index)> Points);

/// <summary>
/// 轮询批量读规划（P2-3）：同一 PLC 的读点位按区码 + 偏移排序，相邻间隔不超过 maxGap 个字、
/// 块总长不超过 maxLength 的合并为一次读，减少逐点往返。地址无法解析的点位单独成块（读时照旧报错）。
/// </summary>
public static class RegisterReadPlanner
{
    /// <summary>合并时允许夹带的未用字数：信号表多为隔字排布（D1000/D1002…）。</summary>
    public const int DefaultMaxGap = 4;

    /// <summary>单次读字数上限：远小于 FINS(999)/Modbus(125) 协议上限，控制单帧耗时。</summary>
    public const int DefaultMaxLength = 64;

    public static IReadOnlyList<RegisterReadBlock> Plan(
        IEnumerable<PlcPointDefinition> points, int maxGap = DefaultMaxGap, int maxLength = DefaultMaxLength)
    {
        var blocks = new List<RegisterReadBlock>();
        var parsed = new List<(PlcPointDefinition Point, char Area, int Offset, int Length)>();
        foreach (var point in points)
        {
            var length = Math.Max(1, point.DataLength);
            try
            {
                var (area, offset) = RegisterAddress.Parse(point.RegisterAddress);
                parsed.Add((point, area, offset, length));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                blocks.Add(new RegisterReadBlock(point.RegisterAddress, length, new[] { (point, 0) }));
            }
        }

        foreach (var area in parsed.GroupBy(x => x.Area))
        {
            var start = 0;
            var end = 0; // 块内最后一个字的下一个偏移
            List<(PlcPointDefinition Point, int Index)>? members = null;
            foreach (var x in area.OrderBy(x => x.Offset).ThenBy(x => x.Length))
            {
                var mergedEnd = Math.Max(end, x.Offset + x.Length);
                if (members is not null && x.Offset - end <= maxGap && mergedEnd - start <= maxLength)
                {
                    members.Add((x.Point, x.Offset - start));
                    end = mergedEnd;
                    continue;
                }
                if (members is not null)
                    blocks.Add(new RegisterReadBlock($"{area.Key}{start}", end - start, members));
                start = x.Offset;
                end = x.Offset + x.Length;
                members = new List<(PlcPointDefinition Point, int Index)> { (x.Point, 0) };
            }
            if (members is not null)
                blocks.Add(new RegisterReadBlock($"{area.Key}{start}", end - start, members));
        }
        return blocks;
    }
}
