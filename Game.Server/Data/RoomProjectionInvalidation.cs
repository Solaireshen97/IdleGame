using System.Data.Common;
using System.Runtime.CompilerServices;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Game.Server.Data;

public sealed class RoomProjectionInvalidation(RoomProjectionRevision revision) : SaveChangesInterceptor
{
    private sealed class Pending
    {
        public bool SaveChanges;
        public HashSet<Guid> Transactions { get; } = [];
    }
    private readonly ConditionalWeakTable<DbContext, Pending> _pending = new();

    private void BeforeSave(DbContext? context)
    {
        if (context is null) return;
        var pending = _pending.GetOrCreateValue(context);
        pending.SaveChanges = context.ChangeTracker.Entries().Any(entry =>
            entry.State is EntityState.Added or EntityState.Deleted ||
            entry.State == EntityState.Modified && (entry.Entity is not RoomSlot ||
                entry.Properties.Any(property => property.IsModified && property.Metadata.Name != nameof(RoomSlot.LastSeenAtUtc))));
        if (pending.SaveChanges && context.Database.CurrentTransaction is { } transaction)
            pending.Transactions.Add(transaction.TransactionId);
    }

    private void AfterSave(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var pending)) return;
        if (pending.SaveChanges) revision.Changed();
        pending.SaveChanges = false;
    }

    public void TransactionCommitted(DbContext? context, Guid transactionId)
    {
        if (context is null || !_pending.TryGetValue(context, out var pending)) return;
        // Explicit transactions can commit long after SavedChanges. Invalidate again
        // so a concurrent reader cannot retain pre-commit data under the new revision.
        if (pending.Transactions.Remove(transactionId) || pending.SaveChanges) revision.Changed();
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    { BeforeSave(eventData.Context); return result; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { BeforeSave(eventData.Context); return ValueTask.FromResult(result); }
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    { AfterSave(eventData.Context); return result; }
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    { AfterSave(eventData.Context); return ValueTask.FromResult(result); }
}

public sealed class RoomProjectionTransactionInvalidation(RoomProjectionInvalidation invalidation) : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        invalidation.TransactionCommitted(eventData.Context, eventData.TransactionId);
    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        invalidation.TransactionCommitted(eventData.Context, eventData.TransactionId);
        return Task.CompletedTask;
    }
}
