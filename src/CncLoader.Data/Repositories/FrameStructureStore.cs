using CncLoader.Core.Abstractions;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

/// <summary>料架改层/删架结构持久化（行为保持自 FrameService EF 路径）。</summary>
public sealed class FrameStructureStore : IFrameStructureStore
{
    private readonly IDbContextFactory<CncDbContext> _factory;

    public FrameStructureStore(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public async Task<FrameStructureRow?> FindAsync(long frameId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var f = await db.Frames.AsNoTracking().FirstOrDefaultAsync(x => x.Id == frameId, ct);
        return f is null
            ? null
            : new FrameStructureRow(f.Id, f.FrameName, f.FrameCode, f.FrameIdentifyCode,
                f.LayerTotal, f.SlotsPerLayer, f.State);
    }

    public async Task<int> CountNonEmptySlotsAsync(long frameId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.FrameSlots.AsNoTracking()
            .CountAsync(s => s.FrameId == frameId && s.SlotState != "0", ct);
    }

    public async Task<int> CountActiveBindsAsync(long frameId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.FrameBinds.AsNoTracking()
            .CountAsync(b => b.FrameId == frameId && b.State == ConfigFlags.Active, ct);
    }

    public async Task ApplyUpdateAsync(FrameStructureUpdate update, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var frame = await db.Frames.FirstOrDefaultAsync(f => f.Id == update.Id, ct);
        if (frame is null) throw new InvalidOperationException($"料架 {update.Id} 不存在");

        frame.FrameName = update.Name;
        frame.FrameCode = update.Code;
        frame.FrameIdentifyCode = update.IdentifyCode;
        frame.Author = update.Author;
        frame.UpdateTime = DateTime.Now;

        if (update.RebuildEmptySlots)
        {
            var old = await db.FrameSlots.Where(s => s.FrameId == update.Id).ToListAsync(ct);
            db.FrameSlots.RemoveRange(old);
            var total = update.LayerTotal * update.SlotsPerLayer;
            for (var slotNo = 1; slotNo <= total; slotNo++)
            {
                db.FrameSlots.Add(new FrameSlot
                {
                    FrameId = update.Id,
                    SlotNo = slotNo,
                    LayerNo = (slotNo - 1) / update.SlotsPerLayer + 1,
                    PosInLayer = (slotNo - 1) % update.SlotsPerLayer + 1,
                    SlotState = "0",
                    UpdateTime = DateTime.Now
                });
            }

            frame.LayerTotal = update.LayerTotal;
            frame.SlotsPerLayer = update.SlotsPerLayer;
            frame.SlotTotal = total;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task SoftDeleteAsync(long frameId, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var frame = await db.Frames.FirstOrDefaultAsync(f => f.Id == frameId, ct);
        if (frame is null) return;
        frame.State = "1";
        frame.Author = author;
        frame.UpdateTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }
}
