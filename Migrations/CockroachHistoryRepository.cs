using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Internal;

namespace Coflnet.Sky.EventBroker.Migrations;

/// <summary>
/// CockroachDB does not support `LOCK TABLE ... IN ACCESS EXCLUSIVE MODE`, which the Npgsql
/// provider emits to guard concurrent migrations since EF Core 9. Hand back a no-op lock instead.
/// </summary>
public class CockroachHistoryRepository : NpgsqlHistoryRepository
{
    public CockroachHistoryRepository(HistoryRepositoryDependencies dependencies) : base(dependencies)
    {
    }

    public override LockReleaseBehavior LockReleaseBehavior => LockReleaseBehavior.Explicit;

    public override IMigrationsDatabaseLock AcquireDatabaseLock()
        => new NoopMigrationsDatabaseLock(this);

    public override Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IMigrationsDatabaseLock>(new NoopMigrationsDatabaseLock(this));

    private sealed class NoopMigrationsDatabaseLock : IMigrationsDatabaseLock
    {
        public NoopMigrationsDatabaseLock(IHistoryRepository historyRepository)
        {
            HistoryRepository = historyRepository;
        }

        public IHistoryRepository HistoryRepository { get; }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}
