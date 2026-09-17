using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Core.Plc;
using CncLoader.Core.State;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>机台 Surface + 产线流。集合写入由调用方保证在 UI 线程。</summary>
public sealed partial class DashboardMachineSurfaceViewModel : ObservableObject
{
    private readonly ISignalStateStore _store;
    private readonly Dictionary<long, MachineCardVm> _machines = new();
    private readonly Dictionary<(long Eq, long Pos), PositionCardVm> _positions = new();
    private readonly Dictionary<long, string> _equipmentNames;
    private readonly Dictionary<long, string> _equipmentNos;
    private IReadOnlyDictionary<long, int> _processOrder;

    internal DashboardMachineSurfaceViewModel(
        ISignalStateStore store,
        Dictionary<long, string> equipmentNames,
        Dictionary<long, string> equipmentNos)
    {
        _store = store;
        _equipmentNames = equipmentNames;
        _equipmentNos = equipmentNos;
        _processOrder = new Dictionary<long, int>();
        Machines = new ObservableCollection<MachineCardVm>();
        FlowNodes = new ObservableCollection<FlowNodeVm>();
    }

    public ObservableCollection<MachineCardVm> Machines { get; }
    public ObservableCollection<FlowNodeVm> FlowNodes { get; }

    [ObservableProperty] private long? _selectedFlowEquipmentId;
    [ObservableProperty] private string _flowLineCode = "—";
    [ObservableProperty] private int _onlineMachines;
    [ObservableProperty] private int _totalMachines;
    [ObservableProperty] private string _onlineSubText = "";

    internal int PositionSignalCount => _positions.Count;

    internal void SetProcessOrder(IReadOnlyDictionary<long, int> order) => _processOrder = order;

    [RelayCommand]
    private void SelectFlowNode(FlowNodeVm? node)
    {
        if (node is null || !node.IsEquipment) { SelectedFlowEquipmentId = null; ApplyMachineHighlight(); return; }
        SelectedFlowEquipmentId = SelectedFlowEquipmentId == node.EquipmentId ? null : node.EquipmentId;
        ApplyMachineHighlight();
    }

    public void ApplyMachineHighlight()
    {
        foreach (var m in Machines)
            m.IsHighlighted = SelectedFlowEquipmentId is long id && m.EquipmentId == id;
        foreach (var n in FlowNodes)
            n.IsSelected = n.IsEquipment && SelectedFlowEquipmentId is long id && n.EquipmentId == id;
    }

    public void UpdateMachinesUi()
    {
        var positions = _store.GetAllPositions();
        var machineSnaps = _store.GetAllMachines().ToDictionary(m => m.EquipmentId);
        var seenEq = new HashSet<long>();
        var seenPos = new HashSet<(long, long)>();

        foreach (var group in positions.GroupBy(p => p.EquipmentId).OrderBy(g => g.Key))
        {
            var eqId = group.Key;
            seenEq.Add(eqId);
            machineSnaps.TryGetValue(eqId, out var ms);
            var name = _equipmentNames.TryGetValue(eqId, out var n) ? n : $"EQ{eqId}";

            if (!_machines.TryGetValue(eqId, out var card))
            {
                card = new MachineCardVm(eqId, name, EquipmentNoOf(eqId));
                _machines[eqId] = card;
                Machines.Add(card);
            }
            card.UpdateIdentity(name, EquipmentNoOf(eqId));
            card.Update(ms?.PlcOnline ?? false, ms?.Safe, ms?.DoorOpen);
            card.IsHighlighted = SelectedFlowEquipmentId == eqId;

            foreach (var p in group.OrderBy(x => x.PositionId))
            {
                var key = (p.EquipmentId, p.PositionId);
                seenPos.Add(key);
                if (_positions.TryGetValue(key, out var pos))
                {
                    pos.Update(p.State, p.MaterialId, p.StatusDetail,
                        ms?.PlcOnline ?? false, ms?.Safe, ms?.DoorOpen);
                }
                else
                {
                    var posVm = new PositionCardVm(p.EquipmentId, p.PositionId, p.State, p.MaterialId, p.StatusDetail,
                        ms?.PlcOnline ?? false, ms?.Safe, ms?.DoorOpen);
                    _positions[key] = posVm;
                    card.Positions.Add(posVm);
                }
            }
        }

        foreach (var ms in machineSnaps.Values.OrderBy(m => m.EquipmentId))
        {
            if (seenEq.Contains(ms.EquipmentId)) continue;
            seenEq.Add(ms.EquipmentId);
            var name = _equipmentNames.TryGetValue(ms.EquipmentId, out var n) ? n : $"EQ{ms.EquipmentId}";
            if (!_machines.TryGetValue(ms.EquipmentId, out var card))
            {
                card = new MachineCardVm(ms.EquipmentId, name, EquipmentNoOf(ms.EquipmentId));
                _machines[ms.EquipmentId] = card;
                Machines.Add(card);
            }
            card.UpdateIdentity(name, EquipmentNoOf(ms.EquipmentId));
            card.Update(ms.PlcOnline, ms.Safe, ms.DoorOpen);
            card.IsHighlighted = SelectedFlowEquipmentId == ms.EquipmentId;
        }

        foreach (var key in _positions.Keys.Where(k => !seenPos.Contains(k)).ToList())
        {
            var pos = _positions[key];
            if (_machines.TryGetValue(key.Eq, out var m)) m.Positions.Remove(pos);
            _positions.Remove(key);
        }
        foreach (var eqId in _machines.Keys.Where(k => !seenEq.Contains(k)).ToList())
        {
            Machines.Remove(_machines[eqId]);
            _machines.Remove(eqId);
        }

        var orderedMachines = DashboardSurfaceProjector.OrderByProcess(Machines, m => m.EquipmentId, _processOrder);
        if (!Machines.SequenceEqual(orderedMachines))
        {
            Machines.Clear();
            foreach (var m in orderedMachines) Machines.Add(m);
        }

        TotalMachines = Machines.Count;
        OnlineMachines = Machines.Count(m => m.PlcOnline);
        OnlineSubText = TotalMachines == 0
            ? "无加工位"
            : $"{_positions.Count} 个加工位有信号";

        RebuildFlowNodes();
    }

    private string EquipmentNoOf(long equipmentId)
        => _equipmentNos.TryGetValue(equipmentId, out var no) ? no : $"EQ{equipmentId:D2}";

    private void RebuildFlowNodes()
    {
        var desired = DashboardSurfaceProjector.BuildDesiredFlowNodes(
            DashboardSurfaceProjector.OrderByProcess(Machines, x => x.EquipmentId, _processOrder));

        while (FlowNodes.Count > desired.Count) FlowNodes.RemoveAt(FlowNodes.Count - 1);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < FlowNodes.Count)
            {
                var cur = FlowNodes[i];
                var next = desired[i];
                if (cur.Key != next.Key)
                {
                    FlowNodes[i] = next;
                    cur = next;
                }
                else
                {
                    if (cur.Title != next.Title)
                        cur.Title = next.Title;
                    if (cur.EquipmentCode != next.EquipmentCode)
                        cur.EquipmentCode = next.EquipmentCode;
                }
                cur.IsEndFork = next.IsEndFork;
            }
            else FlowNodes.Add(desired[i]);
        }

        for (var i = 0; i < FlowNodes.Count; i++)
        {
            var node = FlowNodes[i];
            node.ShowArrowAfter = i < FlowNodes.Count - 1;
            node.ArrowActive = false;
            node.UnloadArrowActive = false;
            node.NgArrowActive = false;

            if (node.IsEndFork)
            {
                node.AggregateDisplay = "下料出站";
                node.SummaryText = "合格出站";
                node.StateBadge = "ok";
                node.ForkSecondaryTitle = "NG出站";
                node.ForkSecondaryAggregate = "不良出站";
                node.ForkSecondarySummary = "不良出站";
                node.ForkSecondaryBadge = "ng";
                node.Seg1Text = "—";
                node.Seg1Badge = "idle";
                node.Seg2Text = "—";
                node.Seg2Badge = "idle";
                node.IsSelected = false;
                continue;
            }

            if (node.IsEquipment && node.EquipmentId is long eqId && _machines.TryGetValue(eqId, out var mach))
            {
                DashboardSurfaceProjector.ApplyEquipmentAggregate(node, mach);
                var inboundBusy = mach.Positions.Any(p =>
                    p.State is PositionState.Dispatching or PositionState.Transporting);
                if (i > 0) FlowNodes[i - 1].ArrowActive = inboundBusy;
            }
            else
            {
                node.AggregateDisplay = "示意";
                node.SummaryText = "流向起点";
                node.StateBadge = "idle";
                node.Seg1Text = "起点";
                node.Seg1Badge = "idle";
                node.Seg2Text = "—";
                node.Seg2Badge = "idle";
            }

            node.IsSelected = node.IsEquipment && SelectedFlowEquipmentId == node.EquipmentId;
        }

        var fork = FlowNodes.LastOrDefault(n => n.IsEndFork);
        var lastEq = FlowNodes.LastOrDefault(n => n.IsEquipment);
        if (fork is not null && lastEq?.EquipmentId is long lastId
            && _machines.TryGetValue(lastId, out var lastMach))
        {
            var outbound = lastMach.Positions.Any(p =>
                p.State is PositionState.Dispatching or PositionState.Transporting);
            var toNg = lastMach.Positions.Any(p => p.State == PositionState.DoneNg);
            var toOk = lastMach.Positions.Any(p => p.State is PositionState.DoneOk or PositionState.Unloaded);
            fork.UnloadArrowActive = outbound || toOk;
            fork.NgArrowActive = outbound || toNg;
            lastEq.ArrowActive = fork.UnloadArrowActive || fork.NgArrowActive;
        }
    }
}
