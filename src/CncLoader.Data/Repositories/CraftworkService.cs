using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

public sealed class CraftworkService : ICraftworkService
{
    private readonly IDbContextFactory<CncDbContext> _factory;
    public CraftworkService(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public async Task<IReadOnlyList<NamedOption>> GetWorkLineOptionsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var lines = await db.WorkLines.AsNoTracking()
            .Where(l => l.State == ConfigFlags.Active)
            .OrderBy(l => l.Id).ToListAsync(ct);
        return lines.Select(l => new NamedOption(l.Id, $"{l.WorkMachineLine} ({l.WorkLineCode})")).ToList();
    }

    public async Task<IReadOnlyList<CraftworkListItem>> GetByLineAsync(long? workLineId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.Craftworks.AsNoTracking().Where(c => c.State == ConfigFlags.Active);
        if (workLineId is > 0) query = query.Where(c => c.WorkLineId == workLineId);
        var crafts = await query.OrderBy(c => c.CraftworkNode).ThenBy(c => c.Id).ToListAsync(ct);
        var lines = await db.WorkLines.AsNoTracking().Where(l => l.State == ConfigFlags.Active).ToListAsync(ct);

        return crafts.Select(c =>
        {
            var line = lines.FirstOrDefault(l => l.Id == c.WorkLineId);
            var quality = ConfigFlags.IsTrue(c.SfQuality);
            return new CraftworkListItem(
                c.Id,
                c.CraftworkNode ?? 0,
                c.CraftworkNo,
                c.CraftworkName,
                quality,
                quality ? "品质检测" : "加工",
                line?.WorkMachineLine ?? "—",
                ConfigFlags.IsTrue(c.SfAutoSend),
                c.CraftworkPrior ?? 0,
                ConfigFlags.IsEnabled(c.State));
        }).ToList();
    }

    public async Task<CraftworkEditModel?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var c = await db.Craftworks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return c is null ? null : new CraftworkEditModel
        {
            Id = c.Id,
            WorkLineId = c.WorkLineId,
            No = c.CraftworkNo,
            Name = c.CraftworkName,
            IsQuality = ConfigFlags.IsTrue(c.SfQuality),
            Sort = c.CraftworkNode ?? 0,
            Prior = c.CraftworkPrior ?? 0,
            AutoSend = ConfigFlags.IsTrue(c.SfAutoSend),
            Enabled = ConfigFlags.IsEnabled(c.State)
        };
    }

    public async Task<long> SaveAsync(CraftworkEditModel model, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = model.Id > 0
            ? await db.Craftworks.FirstOrDefaultAsync(x => x.Id == model.Id, ct)
            : null;
        if (entity is null)
        {
            entity = new Craftwork();
            db.Craftworks.Add(entity);
        }
        entity.WorkLineId = model.WorkLineId;
        entity.CraftworkNo = model.No;
        entity.CraftworkName = model.Name;
        entity.SfQuality = ConfigFlags.ToFlag(model.IsQuality);
        entity.CraftworkNode = model.Sort;
        entity.CraftworkPrior = model.Prior;
        entity.SfAutoSend = ConfigFlags.ToFlag(model.AutoSend);
        entity.State = ConfigFlags.ToState(model.Enabled);
        entity.Author = author;
        entity.UpdateTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    public async Task<DeleteCheckResult> CheckDeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await CheckDeleteCoreAsync(db, id, ct);
    }

    private static async Task<DeleteCheckResult> CheckDeleteCoreAsync(
        CncDbContext db, long id, CancellationToken ct)
    {
        var refs = await db.Equipments.AsNoTracking()
            .CountAsync(e => e.CraftworkId == id && e.State == ConfigFlags.Active, ct);
        return refs == 0
            ? new DeleteCheckResult(true, 0, "可删除")
            : new DeleteCheckResult(false, refs, $"被 {refs} 台机台引用，禁止删除");
    }

    public Task DeleteAsync(long id, string author, CancellationToken ct = default)
        => ConfigSoftDelete.RunAsync(_factory, async (db, token) =>
        {
            var check = await CheckDeleteCoreAsync(db, id, token);
            if (!check.CanDelete) throw new InvalidOperationException(check.Message);

            var entity = await db.Craftworks.FirstOrDefaultAsync(x => x.Id == id && x.State == ConfigFlags.Active, token)
                ?? throw new InvalidOperationException("工序不存在或已删除。");
            entity.State = ConfigFlags.Disabled;
            entity.Author = author;
            entity.UpdateTime = DateTime.Now;
        }, ct);
}
