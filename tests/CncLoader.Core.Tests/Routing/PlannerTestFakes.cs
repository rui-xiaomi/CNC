using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;

namespace CncLoader.Core.Tests.Routing;

internal sealed class MemoryDispatchQueue : IDispatchQueue
{
    private readonly Queue<DispatchItem> _items = new();
    public void Enqueue(DispatchItem item) => _items.Enqueue(item);
    public DispatchItem? Dequeue() => _items.Count == 0 ? null : _items.Dequeue();
    public int Count => _items.Count;
    public void Clear() => _items.Clear();
}

internal sealed class PlannerSlots : ISlotAccountService
{
    public int Occupied { get; set; }

    public Task<FrameOccupancy> GetOccupancyAsync(long frameId, CancellationToken ct = default)
        => Task.FromResult(new FrameOccupancy(10, Occupied, 0, 10 - Occupied));

    public Task<ReservedSlot?> ReserveAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default)
        => Task.FromResult<ReservedSlot?>(null);
    public Task<bool> ConfirmAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> RollbackAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
    public Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default)
        => Task.FromResult<ReservedSlot?>(null);
    public Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
    public Task<int> RollbackStaleReservationsAsync(IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
        => Task.FromResult(0);
    public Task<IReadOnlyList<CompletedPendingConfirm>> ListCompletedPendingConfirmAsync(
        IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CompletedPendingConfirm>>(Array.Empty<CompletedPendingConfirm>());
    public Task<SlotMutationResult> SetSlotAsync(long frameId, int slotNo, string? materialId, string slotState, string author, CancellationToken ct = default)
        => Task.FromResult(SlotMutationResult.From(SlotMutationStatus.NotFound, frameId, slotNo, null));
    public Task<SlotLocation?> LocateMaterialAsync(string materialId, CancellationToken ct = default)
        => Task.FromResult<SlotLocation?>(null);
    public Task<InventoryCorrectionResult> CorrectFromInventoryAsync(long frameId, int posStart, IReadOnlyList<string> products, CancellationToken ct = default)
        => Task.FromResult(InventoryCorrectionResult.Empty());
    public Task<IReadOnlyList<SlotRecord>> GetSlotsAsync(long frameId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SlotRecord>>(Array.Empty<SlotRecord>());
}

internal sealed class PlannerEquipment : IEquipmentConfigService
{
    public long? UploadFrameId { get; set; }
    public long? TransitFrameId { get; set; }
    public long? DownloadFrameId { get; set; }
    public long? NgFrameId { get; set; }
    public bool HasSubsequent { get; set; }
    public IReadOnlyList<long> NextEquipments { get; set; } = Array.Empty<long>();

    public Task<long?> GetFrameBindingByRoleAsync(long equipmentId, FrameRole role, CancellationToken ct = default)
        => Task.FromResult(role switch
        {
            FrameRole.Upload => UploadFrameId,
            FrameRole.Transit => TransitFrameId,
            FrameRole.Unload => DownloadFrameId,
            FrameRole.NgFrame => NgFrameId,
            _ => null
        });

    public Task<IReadOnlyList<long>> GetNextProcessEquipmentsAsync(long equipmentId, CancellationToken ct = default)
        => Task.FromResult(NextEquipments);
    public Task<bool> HasSubsequentProcessAsync(long equipmentId, CancellationToken ct = default)
        => Task.FromResult(HasSubsequent);
    public Task<EquipmentFrameBindingIds> GetFrameBindingIdsAsync(long equipmentId, CancellationToken ct = default)
        => Task.FromResult(new EquipmentFrameBindingIds(UploadFrameId, DownloadFrameId));
    public Task<WorkLineRef?> GetWorkLineByEquipmentAsync(long equipmentId, CancellationToken ct = default)
        => Task.FromResult<WorkLineRef?>(new WorkLineRef(10, "LINE-A"));

    public Task<IReadOnlyList<NamedOption>> GetCraftworkOptionsAsync(CancellationToken ct = default)
        => Empty<NamedOption>();
    public Task<IReadOnlyList<EquipmentListItem>> GetByCraftAsync(long? craftworkId, CancellationToken ct = default)
        => Empty<EquipmentListItem>();
    public Task<IReadOnlyList<PositionItem>> GetPositionsAsync(long equipmentId, CancellationToken ct = default)
        => Empty<PositionItem>();
    public Task<IReadOnlyList<EquipmentFrameBinding>> GetFrameBindingsAsync(long equipmentId, CancellationToken ct = default)
        => Empty<EquipmentFrameBinding>();
    public Task<IReadOnlyList<NamedOption>> GetPlcOptionsAsync(CancellationToken ct = default) => Empty<NamedOption>();
    public Task<IReadOnlyList<NamedOption>> GetFrameOptionsAsync(CancellationToken ct = default) => Empty<NamedOption>();
    public Task<string> SuggestNextNoAsync(CancellationToken ct = default) => Task.FromResult("EQ01");
    public Task<long> CreateEquipmentAsync(EquipmentCreateModel model, string author, CancellationToken ct = default)
        => Task.FromResult(0L);
    public Task<EquipmentEditModel?> GetByIdAsync(long equipmentId, CancellationToken ct = default)
        => Task.FromResult<EquipmentEditModel?>(null);
    public Task UpdateAsync(EquipmentEditModel model, string author, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<FrameBindingInfo>> GetBindingByFrameAsync(long frameId, CancellationToken ct = default)
        => Empty<FrameBindingInfo>();
    public Task SetFrameBindingAsync(long equipmentId, long? uploadFrameId, long? downloadFrameId, string author, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<DeleteCheckResult> CheckDeleteAsync(long equipmentId, CancellationToken ct = default)
        => Task.FromResult(new DeleteCheckResult(true, 0, ""));
    public Task DeleteAsync(long equipmentId, string author, CancellationToken ct = default) => Task.CompletedTask;

    private static Task<IReadOnlyList<T>> Empty<T>() => Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());
}
