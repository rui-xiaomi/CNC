using CncLoader.Common.Configuration;
using CncLoader.Common.DependencyInjection;
using CncLoader.Data;
using CncLoader.Data.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
services.AddCncCommon(configuration);
services.AddCncData();
await using var sp = services.BuildServiceProvider();
await using var db = await sp.GetRequiredService<IDbContextFactory<CncDbContext>>().CreateDbContextAsync();

const long loadFrameId = 1;
var electrodes = Enumerable.Range(1, 10).Select(i => $"EL-{i:D3}").ToArray();

await using var tx = await db.Database.BeginTransactionAsync();

var all = await db.FrameSlots.ToListAsync();
foreach (var s in all)
{
    s.SlotState = "0";
    s.ElectrodeId = null;
    s.BindSource = null;
    s.BindTime = null;
    s.LastVerifyTime = null;
    s.Remark = null;
}

var loadSlots = all.Where(s => s.FrameId == loadFrameId).OrderBy(s => s.SlotNo).ToList();
for (var i = 0; i < Math.Min(electrodes.Length, loadSlots.Count); i++)
{
    var slot = loadSlots[i];
    slot.SlotState = "1";
    slot.ElectrodeId = electrodes[i];
    slot.BindSource = "MANUAL";
    slot.BindTime = DateTime.Now;
}

await db.SaveChangesAsync();
await tx.CommitAsync();

var summary = await db.FrameSlots
    .GroupBy(s => s.FrameId)
    .Select(g => new { FrameId = g.Key, Total = g.Count(), Occupied = g.Count(x => x.SlotState == "1") })
    .OrderBy(x => x.FrameId)
    .ToListAsync();

Console.WriteLine("料架槽位已清空，上料总架(ID=1)已写入 EL-001..010：");
foreach (var row in summary)
    Console.WriteLine($"  frame {row.FrameId}: 占用 {row.Occupied}/{row.Total}");

var load = await db.FrameSlots.AsNoTracking()
    .Where(s => s.FrameId == loadFrameId)
    .OrderBy(s => s.SlotNo)
    .Select(s => new { s.SlotNo, s.ElectrodeId, s.SlotState })
    .ToListAsync();
Console.WriteLine("上料总架明细：");
foreach (var s in load)
    Console.WriteLine($"  槽{s.SlotNo}: state={s.SlotState} electrode={s.ElectrodeId ?? "(空)"}");
