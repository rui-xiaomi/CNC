using CncLoader.Core.State;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>
/// 看板只读投影：机台工序排序、产线流节点与工位聚合。集合写入仍由 ViewModel 经 Dispatcher 完成。
/// </summary>
internal static class DashboardSurfaceProjector
{
    public static IReadOnlyList<T> OrderByProcess<T>(
        IEnumerable<T> items, Func<T, long> idOf, IReadOnlyDictionary<long, int> order)
        => items
            .OrderBy(x => order.TryGetValue(idOf(x), out var n) ? n : int.MaxValue)
            .ThenBy(idOf)
            .ToList();

    public static IReadOnlyList<FlowNodeVm> BuildDesiredFlowNodes(IEnumerable<MachineCardVm> machines)
    {
        var desired = new List<FlowNodeVm>
        {
            new("upload", "上料架", null, isAnchor: true)
        };
        foreach (var m in machines)
            desired.Add(new FlowNodeVm($"eq-{m.EquipmentId}", m.Name, m.EquipmentId, isAnchor: false, m.EquipmentCode));
        desired.Add(new("ends", "终点", null, isAnchor: true) { IsEndFork = true });
        return desired;
    }

    public static void ApplyEquipmentAggregate(FlowNodeVm node, MachineCardVm mach)
    {
        var positions = mach.Positions.OrderBy(p => p.PositionId).ToList();
        if (positions.Count == 0)
        {
            node.AggregateDisplay = mach.PlcOnline ? "等待上料" : "离线";
            node.StateBadge = mach.PlcOnline ? "idle" : "offline";
            node.SummaryText = "无加工位";
            node.Seg1Text = "—";
            node.Seg1Badge = "idle";
            node.Seg2Text = "—";
            node.Seg2Badge = "idle";
            return;
        }

        var agg = AggregatePriority(positions.Select(p => p.State));
        node.AggregateDisplay = PositionStateNames.ToDisplay(agg);
        node.StateBadge = BadgeForAggregate(agg);

        var parts = new List<string>();
        void Add(string label, Func<PositionCardVm, bool> pred)
        {
            var n = positions.Count(pred);
            if (n > 0) parts.Add($"{n} {label}");
        }
        Add("报警", p => p.State == PositionState.Alarm);
        Add("检测中", p => p.State == PositionState.Processing);
        Add("搬运", p => p.State is PositionState.Dispatching or PositionState.Transporting);
        Add("已上料", p => p.State == PositionState.Loaded);
        Add("待料", p => p.State is PositionState.WaitLoad or PositionState.Unloaded);
        Add("OK", p => p.State == PositionState.DoneOk);
        Add("NG", p => p.State == PositionState.DoneNg);
        Add("离线", p => p.State == PositionState.Offline);
        node.SummaryText = parts.Count > 0 ? string.Join(" · ", parts.Take(2)) : "—";

        var (t1, b1) = SegFor(positions[0].State);
        node.Seg1Text = t1;
        node.Seg1Badge = b1;
        if (positions.Count > 1)
        {
            var (t2, b2) = SegFor(positions[1].State);
            node.Seg2Text = t2;
            node.Seg2Badge = b2;
        }
        else
        {
            node.Seg2Text = "—";
            node.Seg2Badge = "idle";
        }
    }

    public static (string Text, string Badge) SegFor(PositionState s) => s switch
    {
        PositionState.Alarm => ("报警", "alarm"),
        PositionState.Processing => ("检测", "run"),
        PositionState.Dispatching or PositionState.Transporting => ("搬运", "run"),
        PositionState.Loaded => ("已上料", "warn"),
        PositionState.DoneOk => ("OK", "ok"),
        PositionState.DoneNg => ("NG", "ng"),
        PositionState.Offline => ("离线", "offline"),
        PositionState.WaitLoad or PositionState.Unloaded => ("待料", "idle"),
        _ => ("—", "idle")
    };

    public static PositionState AggregatePriority(IEnumerable<PositionState> states)
    {
        var list = states.ToList();
        if (list.Count == 0) return PositionState.Offline;
        if (list.Any(s => s == PositionState.Alarm)) return PositionState.Alarm;
        if (list.Any(s => s is PositionState.Processing or PositionState.Dispatching or PositionState.Transporting))
            return list.Any(s => s == PositionState.Processing) ? PositionState.Processing : PositionState.Transporting;
        if (list.Any(s => s == PositionState.Loaded)) return PositionState.Loaded;
        if (list.Any(s => s is PositionState.DoneOk or PositionState.DoneNg))
            return list.Any(s => s == PositionState.DoneNg) ? PositionState.DoneNg : PositionState.DoneOk;
        if (list.Any(s => s is PositionState.WaitLoad or PositionState.Unloaded)) return PositionState.WaitLoad;
        if (list.All(s => s == PositionState.Offline)) return PositionState.Offline;
        return PositionState.WaitLoad;
    }

    public static string BadgeForAggregate(PositionState s) => s switch
    {
        PositionState.Offline => "offline",
        PositionState.Alarm => "alarm",
        PositionState.Processing or PositionState.Dispatching or PositionState.Transporting => "run",
        PositionState.DoneOk => "ok",
        PositionState.DoneNg => "ng",
        PositionState.Loaded => "warn",
        _ => "idle"
    };
}
