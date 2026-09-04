using CncLoader.Core.Rcs;
using CncLoader.Data;
using CncLoader.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CncLoader.Core.Tests.State;

/// <summary>
/// 盘点批量会话：Pomelo <c>EnableRetryOnFailure</c> 下，用户事务内的下一条 EF 操作
/// 会走 <c>ExecutionStrategy.OnFirstExecution</c> 并抛策略门禁。
/// 用可替换的事务管理器避免连真实 MySQL。
/// </summary>
[TestFixture]
public sealed class SlotAccountStoreRetryTransactionTests
{
    private const string StrategyTxMessage = "does not support user-initiated transactions";

    [Test]
    public void 用户事务内查询_重试策略下_抛出策略门禁()
    {
        using var db = new CncDbContext(CreateRetryingOptions(replaceTransactionManager: true));

        Assert.DoesNotThrowAsync(async () => await db.Database.BeginTransactionAsync());

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await db.FrameSlots.AsNoTracking().CountAsync());

        Assert.That(ex!.Message, Does.Contain(StrategyTxMessage));
    }

    [Test]
    public void OpenAsync后查询_生产Store_撞上策略门禁()
    {
        ISlotAccountStore store = new SlotAccountStore(new RetryingFactory());

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var session = await store.OpenAsync();
            await session.FindByFrameOrderedAsync(91);
        });

        Assert.That(ex!.Message, Does.Contain(StrategyTxMessage));
    }

    [Test]
    public async Task ExecuteInTransactionAsync内查询_不得撞策略门禁()
    {
        ISlotAccountStore store = new SlotAccountStore(new RetryingFactory());

        try
        {
            await store.ExecuteInTransactionAsync(async (session, ct) =>
            {
                await session.FindByFrameOrderedAsync(91, ct);
                return 0;
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains(StrategyTxMessage, StringComparison.Ordinal))
        {
            Assert.Fail("盘点批量会话仍被 MySqlRetryingExecutionStrategy 拒绝");
        }
        catch (Exception)
        {
            // 假事务之后的 SQL 仍可能打到空端口；只断言不是策略门禁。
        }
    }

    private static DbContextOptions<CncDbContext> CreateRetryingOptions(bool replaceTransactionManager = true)
    {
        var serverVersion = new MySqlServerVersion(new Version(8, 0, 36));
        var builder = new DbContextOptionsBuilder<CncDbContext>()
            .UseMySql(
                "Server=127.0.0.1;Port=1;Database=cnc_auto;User=cnc;Password=x;Connection Timeout=1",
                serverVersion,
                mySql => mySql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromMilliseconds(10),
                    errorNumbersToAdd: null));
        if (replaceTransactionManager)
            builder.ReplaceService<IDbContextTransactionManager, FakeTransactionManager>();
        return builder.Options;
    }

    private sealed class RetryingFactory : IDbContextFactory<CncDbContext>
    {
        public CncDbContext CreateDbContext() => new(CreateRetryingOptions());

        public Task<CncDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class FakeTransactionManager : IDbContextTransactionManager
    {
        private IDbContextTransaction? _current;

        public IDbContextTransaction? CurrentTransaction => _current;

        public IDbContextTransaction BeginTransaction()
        {
            _current = new FakeTransaction(() => _current = null);
            return _current;
        }

        public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(BeginTransaction());

        public void CommitTransaction() => _current?.Commit();

        public Task CommitTransactionAsync(CancellationToken cancellationToken = default)
        {
            _current?.Commit();
            return Task.CompletedTask;
        }

        public void RollbackTransaction() => _current?.Rollback();

        public Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
        {
            _current?.Rollback();
            return Task.CompletedTask;
        }

        public void ResetState() => _current = null;

        public Task ResetStateAsync(CancellationToken cancellationToken = default)
        {
            ResetState();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTransaction(Action onDispose) : IDbContextTransaction
    {
        public Guid TransactionId { get; } = Guid.NewGuid();

        public void Commit() { }

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Rollback() { }

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose() => onDispose();

        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }
}
