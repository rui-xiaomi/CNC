using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

public sealed class WorkLineService : IWorkLineService
{
    private readonly IDbContextFactory<CncDbContext> _factory;
    public WorkLineService(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public event EventHandler? WorkLinesChanged;

    public async Task<IReadOnlyList<WorkLineListItem>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var lines = await db.WorkLines.AsNoTracking()
            .Where(l => l.State == ConfigFlags.Active)
            .OrderBy(l => l.Id).ToListAsync(ct);
        return lines.Select(l => new WorkLineListItem(
            l.Id,
            l.WorkLineCode,
            l.WorkMachineLine,
            ConfigFlags.IsTrue(l.ScanState),
            ConfigFlags.IsEnabled(l.State),
            l.AgvId)).ToList();
    }

    public async Task<WorkLineEditModel?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var l = await db.WorkLines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return l is null ? null : new WorkLineEditModel
        {
            Id = l.Id,
            Name = l.WorkMachineLine,
            Code = l.WorkLineCode,
            Computer = l.WorkLineComputer,
            ComputerIp = l.WorkLineComputerIp,
            PlanNum = l.PlanWorkNum,
            ScanEnabled = ConfigFlags.IsTrue(l.ScanState),
            Enabled = ConfigFlags.IsEnabled(l.State)
        };
    }

    public async Task<long> SaveAsync(WorkLineEditModel model, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = model.Id > 0
            ? await db.WorkLines.FirstOrDefaultAsync(x => x.Id == model.Id, ct)
            : null;
        if (entity is null)
        {
            entity = new WorkLineConfig();
            db.WorkLines.Add(entity);
        }
        entity.WorkMachineLine = model.Name;
        entity.WorkLineCode = model.Code;
        entity.WorkLineComputer = model.Computer;
        entity.WorkLineComputerIp = model.ComputerIp;
        entity.PlanWorkNum = model.PlanNum;
        entity.ScanState = ConfigFlags.ToFlag(model.ScanEnabled);
        entity.State = ConfigFlags.ToState(model.Enabled);
        entity.Author = author;
        entity.UpdateTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
        WorkLinesChanged?.Invoke(this, EventArgs.Empty);
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
        var refs = await db.Craftworks.AsNoTracking()
            .CountAsync(c => c.WorkLineId == id && c.State == ConfigFlags.Active, ct);
        return refs == 0
            ? new DeleteCheckResult(true, 0, "可删除")
            : new DeleteCheckResult(false, refs, $"被 {refs} 道工序引用，禁止删除");
    }

    public async Task DeleteAsync(long id, string author, CancellationToken ct = default)
    {
        await ConfigSoftDelete.RunAsync(_factory, async (db, token) =>
        {
            var check = await CheckDeleteCoreAsync(db, id, token);
            if (!check.CanDelete) throw new InvalidOperationException(check.Message);

            var entity = await db.WorkLines.FirstOrDefaultAsync(x => x.Id == id && x.State == ConfigFlags.Active, token)
                ?? throw new InvalidOperationException("线体不存在或已删除。");
            entity.State = ConfigFlags.Disabled;
            entity.Author = author;
            entity.UpdateTime = DateTime.Now;
        }, ct);

        WorkLinesChanged?.Invoke(this, EventArgs.Empty);
    }
}
