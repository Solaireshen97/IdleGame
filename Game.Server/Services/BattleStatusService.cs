using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public readonly record struct BattleStatusSource(string ActorType, int ActorId, string? SkillCode = null);

/// <summary>
/// Owns status instances for both sides. It stages changes in the caller's battle transaction;
/// applying, consuming or displaying a status never saves the database independently.
/// </summary>
public sealed class BattleStatusService(GameDbContext db, BattleStatusCatalog catalog, BattleEventCollector? events = null,
    ProfessionMechanicCatalog? mechanics = null, DungeonRunRulesService? runRules = null)
{
    private readonly ProfessionMechanicDescription _mechanicDescriptions = new(mechanics ?? ProfessionMechanicCatalog.Default);
    public BattleStatusCatalog Catalog => catalog;
    public BattleStatusCatalog CatalogFor(Room room) => runRules?.CombatFor(room).Statuses ?? catalog;
    public async Task PrepareAsync(Room room)
    {
        if (runRules is not null) await runRules.EnsureAsync(room);
    }
    public BattleEventCollector Events { get; } = events ?? new();
    private SettlementScope? _settlement;
    private ReadSnapshotScope? _readSnapshot;

    /// <summary>Captures one request's display state without tracking new status entities.</summary>
    public async Task<IDisposable> BeginReadSnapshotAsync(Room room)
    {
        await PrepareAsync(room);
        if (_readSnapshot is not null) throw new InvalidOperationException("A battle status read snapshot is already active.");
        var effects = SettlementStates(room);
        if (effects is null)
        {
            effects = await db.BattleStatusEffects.AsNoTracking().Where(effect => effect.RoomId == room.Id &&
                effect.RunSequence == room.RunSequence && effect.ExpiresAfterRound >= room.RoundNumber).ToListAsync();
            var byKey = effects.ToDictionary(effect => (effect.TargetType, effect.TargetId, effect.EffectCode));
            foreach (var entry in db.ChangeTracker.Entries<BattleStatusEffect>().Where(entry =>
                         entry.Entity.RoomId == room.Id && entry.Entity.RunSequence == room.RunSequence))
            {
                var effect = entry.Entity;
                var key = (effect.TargetType, effect.TargetId, effect.EffectCode);
                if (entry.State == EntityState.Deleted || effect.ExpiresAfterRound < room.RoundNumber) byKey.Remove(key);
                else byKey[key] = (BattleStatusEffect)entry.CurrentValues.ToObject();
            }
            effects = byKey.Values.ToList();
        }
        var monsterIds = effects.Where(effect => effect.ExpiresAfterRound >= room.RoundNumber &&
                effect.BoundTargetType == "Monster" && effect.BoundTargetId.HasValue)
            .Select(effect => effect.BoundTargetId!.Value).Distinct().ToArray();
        var names = monsterIds.Length == 0 ? new Dictionary<int, string>() : await db.Monsters.AsNoTracking()
            .Where(monster => monsterIds.Contains(monster.Id)).Select(monster => new { monster.Id, monster.Name })
            .ToDictionaryAsync(monster => monster.Id, monster => monster.Name);
        var scope = new ReadSnapshotScope(this, room.Id, room.RunSequence, effects, names);
        _readSnapshot = scope;
        return scope;
    }

    private sealed class ReadSnapshotScope(BattleStatusService owner, int roomId, int runSequence,
        List<BattleStatusEffect> effects, Dictionary<int, string> monsterNames) : IDisposable
    {
        public int RoomId { get; } = roomId;
        public int RunSequence { get; } = runSequence;
        public List<BattleStatusEffect> Effects { get; } = effects;
        public Dictionary<int, string> MonsterNames { get; } = monsterNames;
        public void Dispose()
        {
            if (ReferenceEquals(owner._readSnapshot, this)) owner._readSnapshot = null;
        }
    }

    private ReadSnapshotScope? ReadSnapshot(Room room) => _readSnapshot is { } scope &&
        scope.RoomId == room.Id && scope.RunSequence == room.RunSequence ? scope : null;

    /// <summary>
    /// Loads this challenge once for a settlement. The tracked entities remain authoritative,
    /// including unsaved changes; disposal must cover both successful and failed commits.
    /// Clearing the change tracker aborts the settlement; dispose before further status calls.
    /// </summary>
    public async Task<IDisposable> BeginSettlementAsync(Room room)
    {
        await PrepareAsync(room);
        if (_settlement is not null) throw new InvalidOperationException("A battle status settlement is already active.");
        var scope = new SettlementScope(this, room.Id, room.RunSequence);
        _settlement = scope;
        try
        {
            // Include expired rows so refreshing their unique key reuses the existing instance.
            scope.Effects.AddRange(await db.BattleStatusEffects.Where(effect => effect.RoomId == scope.RoomId &&
                effect.RunSequence == scope.RunSequence).ToListAsync());
            scope.IsLoaded = true;
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    private List<BattleStatusEffect>? SettlementStates(Room room)
    {
        if (_settlement is not { } scope || scope.RoomId != room.Id || scope.RunSequence != room.RunSequence) return null;
        if (!scope.IsLoaded) throw new InvalidOperationException("Battle statuses are still loading.");
        // Keep deleted persisted instances for reapplication before saving. Added-then-removed
        // instances become detached and must not be resurrected as existing database rows.
        scope.Effects.RemoveAll(effect => db.Entry(effect).State == EntityState.Detached);
        foreach (var local in db.BattleStatusEffects.Local.Where(effect => effect.RoomId == scope.RoomId &&
                     effect.RunSequence == scope.RunSequence))
            if (!scope.Effects.Contains(local)) scope.Effects.Add(local);
        return scope.Effects;
    }

    private sealed class SettlementScope(BattleStatusService owner, int roomId, int runSequence) : IDisposable
    {
        public int RoomId { get; } = roomId;
        public int RunSequence { get; } = runSequence;
        public List<BattleStatusEffect> Effects { get; } = [];
        public bool IsLoaded { get; set; }
        public void Dispose()
        {
            if (ReferenceEquals(owner._settlement, this)) owner._settlement = null;
        }
    }

    public Task<List<BattleStatusEffect>> MechanicStatesAsync(Room room, string actorType, int actorId, BattleStatusMechanic mechanic) =>
        FindMechanicStatesAsync(room, actorType, actorId, mechanic);

    private async Task<List<BattleStatusEffect>> FindMechanicStatesAsync(Room room, string actorType, int actorId, BattleStatusMechanic mechanic) =>
        (await GetActiveAsync(room, actorType, [actorId])).Where(effect => CatalogFor(room).Find(effect.EffectCode)?.Mechanic == mechanic).ToList();

    public async Task<decimal> MechanicPowerAsync(Room room, string actorType, int actorId, BattleStatusMechanic mechanic) =>
        (await FindMechanicStatesAsync(room, actorType, actorId, mechanic)).Select(effect =>
            Math.Abs(effect.MagnitudeSnapshot ?? CatalogFor(room).Find(effect.EffectCode)!.FamilyStrength)).DefaultIfEmpty(0).Max();

    public async Task<decimal> ConsumeMechanicPowerAsync(Room room, string actorType, int actorId, BattleStatusMechanic mechanic)
    {
        var effects = await FindMechanicStatesAsync(room, actorType, actorId, mechanic);
        var power = effects.Select(effect => Math.Abs(effect.MagnitudeSnapshot ?? CatalogFor(room).Find(effect.EffectCode)!.FamilyStrength)).DefaultIfEmpty(0).Max();
        RemoveStates(room, effects, BattleStatusChange.Consumed);
        return power;
    }
    public async Task<List<BattleStatusEffect>> GetActiveAsync(Room room, string targetType,
        IReadOnlyCollection<int> targetIds)
    {
        await PrepareAsync(room);
        if (SettlementStates(room) is { } loaded)
            return loaded.Where(effect => effect.TargetType == targetType && targetIds.Contains(effect.TargetId) &&
                effect.ExpiresAfterRound >= room.RoundNumber && db.Entry(effect).State != EntityState.Deleted).ToList();
        if (ReadSnapshot(room) is { } snapshot)
            return snapshot.Effects.Where(effect => effect.TargetType == targetType && targetIds.Contains(effect.TargetId) &&
                effect.ExpiresAfterRound >= room.RoundNumber).ToList();
        var effects = await db.BattleStatusEffects.Where(effect => effect.RoomId == room.Id &&
            effect.RunSequence == room.RunSequence && effect.TargetType == targetType &&
            targetIds.Contains(effect.TargetId) && effect.ExpiresAfterRound >= room.RoundNumber).ToListAsync();
        effects.RemoveAll(effect => db.Entry(effect).State == EntityState.Deleted || effect.ExpiresAfterRound < room.RoundNumber);
        foreach (var local in db.BattleStatusEffects.Local.Where(effect => effect.RoomId == room.Id &&
                     effect.RunSequence == room.RunSequence && effect.TargetType == targetType &&
                     targetIds.Contains(effect.TargetId) && effect.ExpiresAfterRound >= room.RoundNumber &&
                     db.Entry(effect).State != EntityState.Deleted))
            if (!effects.Contains(local)) effects.Add(local);
        return effects;
    }

    public async Task<bool> HasAsync(Room room, string targetType, int targetId, string code) =>
        (await GetActiveAsync(room, targetType, [targetId])).Any(effect =>
            string.Equals(effect.EffectCode, code, StringComparison.OrdinalIgnoreCase));

    public async Task<int> StacksAsync(Room room, string targetType, int targetId, string code) =>
        (await GetActiveAsync(room, targetType, [targetId])).FirstOrDefault(effect =>
            string.Equals(effect.EffectCode, code, StringComparison.OrdinalIgnoreCase))?.Stacks ?? 0;

    public async Task<bool> HasRemovableAsync(Room room, string targetType,
        IReadOnlyCollection<int> targetIds, bool positive) =>
        (await GetActiveAsync(room, targetType, targetIds)).Any(effect => IsRemovable(room, effect, positive));

    public bool IsRemovable(BattleStatusEffect effect, bool positive) =>
        catalog.Find(effect.EffectCode) is { } definition && definition.IsPositive == positive && definition.IsDispellable;

    public bool IsRemovable(Room room, BattleStatusEffect effect, bool positive) =>
        CatalogFor(room).Find(effect.EffectCode) is { } definition && definition.IsPositive == positive && definition.IsDispellable;

    public async Task RemoveAsync(Room room, string targetType, int targetId, string code)
    {
        RemoveStates(room, (await GetActiveAsync(room, targetType, [targetId]))
            .Where(effect => string.Equals(effect.EffectCode, code, StringComparison.OrdinalIgnoreCase)), BattleStatusChange.Removed);
    }

    public async Task RemoveCodesAsync(Room room, string targetType, IReadOnlyCollection<int> targetIds,
        IReadOnlyCollection<string> codes) => RemoveStates(room,
            (await GetActiveAsync(room, targetType, targetIds)).Where(effect => codes.Contains(effect.EffectCode)), BattleStatusChange.Expired);

    public async Task<RemovedBattleStatus?> RemoveFirstAsync(Room room, string targetType,
        IReadOnlyList<int> targetIds, bool positive)
    {
        var order = targetIds.Select((id, index) => (id, index)).ToDictionary(entry => entry.id, entry => entry.index);
        var effect = (await GetActiveAsync(room, targetType, targetIds))
            .OrderBy(effect => order.GetValueOrDefault(effect.TargetId, int.MaxValue)).ThenBy(effect => effect.Id)
            .FirstOrDefault(effect => CatalogFor(room).Find(effect.EffectCode) is { } definition &&
                definition.IsPositive == positive && definition.IsDispellable);
        if (effect is null) return null;
        var definition = CatalogFor(room).Find(effect.EffectCode)!;
        CaptureStatus(room, effect, BattleStatusChange.Removed, effect.Stacks, 0,
            positive ? BattleEventKind.Dispel : BattleEventKind.Cleanse);
        db.BattleStatusEffects.Remove(effect);
        return new(effect.TargetId, definition.Code, definition.Name, definition.IsPositive);
    }

    public async Task<decimal> ModifierAsync(Room room, string targetType, int targetId, string effectType) =>
        (await GetActiveAsync(room, targetType, [targetId])).Sum(effect =>
            CatalogFor(room).Find(effect.EffectCode) is { } definition && definition.EffectType == effectType
                ? (effect.MagnitudeSnapshot ?? definition.ValuePerStack) * effect.Stacks : 0m);

    public async Task<bool> ApplyAsync(Room room, string targetType, int targetId, string code,
        int durationRounds, List<string> logs, string targetLabel, int? perTickValue = null,
        BattleStatusSource? source = null, string? boundTargetType = null, int? boundTargetId = null,
        decimal? magnitudeSnapshot = null, int? counterCount = null)
    {
        await PrepareAsync(room);
        var definition = CatalogFor(room).Find(code);
        if (definition is null) return false;
        if (counterCount.HasValue && (counterCount < 1 || counterCount > definition.MaxStacks)) throw new ArgumentOutOfRangeException(nameof(counterCount));
        if (perTickValue is <= 0) throw new ArgumentOutOfRangeException(nameof(perTickValue));
        if (durationRounds < 0)
            throw new ArgumentOutOfRangeException(nameof(durationRounds));
        if ((boundTargetType is null) != (boundTargetId is null))
            throw new ArgumentException("A bound target needs both its actor type and identity.");
        if (targetType is not ("Character" or "Monster") || targetId <= 0 ||
            source is { } origin && (origin.ActorType is not ("Character" or "Monster") || origin.ActorId <= 0) ||
            boundTargetType is not null && (boundTargetType is not ("Character" or "Monster") || boundTargetId <= 0))
            throw new ArgumentException("A battle status needs valid actor identities.");

        if (definition.FamilyCode is not null)
        {
            var family = (await GetActiveAsync(room, targetType, [targetId])).Where(entry =>
                CatalogFor(room).Find(entry.EffectCode)?.FamilyCode == definition.FamilyCode).ToList();
            var stronger = family.OrderByDescending(entry => Math.Abs(entry.MagnitudeSnapshot ?? CatalogFor(room).Find(entry.EffectCode)!.FamilyStrength))
                .FirstOrDefault(entry => Math.Abs(entry.MagnitudeSnapshot ?? CatalogFor(room).Find(entry.EffectCode)!.FamilyStrength) > Math.Abs(magnitudeSnapshot ?? definition.FamilyStrength));
            if (definition.FamilyRefresh == BattleStatusFamilyRefresh.KeepStronger && stronger is not null)
            {
                CaptureStatus(room, stronger, BattleStatusChange.Retained, stronger.Stacks, stronger.Stacks);
                return true;
            }
            RemoveStates(room, family.Where(entry => entry.EffectCode != definition.Code), BattleStatusChange.Removed);
        }

        var loaded = SettlementStates(room);
        var effect = (loaded ?? db.BattleStatusEffects.Local.ToList()).FirstOrDefault(entry => entry.RoomId == room.Id &&
            entry.RunSequence == room.RunSequence && entry.TargetType == targetType && entry.TargetId == targetId &&
            entry.EffectCode == definition.Code);
        if (effect is null && loaded is null)
            effect = await db.BattleStatusEffects.SingleOrDefaultAsync(entry => entry.RoomId == room.Id &&
                entry.RunSequence == room.RunSequence && entry.TargetType == targetType && entry.TargetId == targetId &&
                entry.EffectCode == definition.Code);
        var active = effect is not null && db.Entry(effect).State != EntityState.Deleted &&
            effect.ExpiresAfterRound >= room.RoundNumber;
        var countBefore = active ? effect!.Stacks : 0;
        if (effect is not null && db.Entry(effect).State == EntityState.Deleted)
            db.Entry(effect).State = EntityState.Modified;
        if (effect is null)
        {
            effect = new BattleStatusEffect { RoomId = room.Id, RunSequence = room.RunSequence,
                TargetType = targetType, TargetId = targetId, EffectCode = definition.Code,
                AppliedRound = room.RoundNumber, Stacks = definition.InitialStacks };
            db.BattleStatusEffects.Add(effect);
        }
        else
        {
            if (!active || definition.CounterKind == BattleStatusCounterKind.Charges) effect.Stacks = definition.InitialStacks;
            else if (definition.Stacking == "AddStack") effect.Stacks = Math.Min(definition.MaxStacks, effect.Stacks + 1);
            // Refreshing an existing periodic effect must not postpone its due tick.
            if (!active || definition.EffectType is not ("DamageOverTime" or "HealOverTime"))
                effect.AppliedRound = room.RoundNumber;
        }
        if (counterCount.HasValue) effect.Stacks = counterCount.Value;
        var updateSource = !active || definition.EffectType is not ("DamageOverTime" or "HealOverTime") ||
            !effect.PerTickValue.HasValue;
        if (!active) effect.PerTickValue = perTickValue;
        else if (perTickValue.HasValue && definition.EffectType is "DamageOverTime" or "HealOverTime")
        {
            var oldPower = effect.PerTickValue ??
                Math.Max(1, (int)decimal.Floor(Math.Abs(definition.ValuePerStack) * effect.Stacks));
            updateSource = definition.SnapshotRefresh == BattleStatusSnapshotRefresh.Replace || perTickValue.Value >= oldPower;
            effect.PerTickValue = definition.SnapshotRefresh == BattleStatusSnapshotRefresh.Replace
                ? perTickValue.Value : Math.Max(oldPower, perTickValue.Value);
        }
        if (!active) effect.MagnitudeSnapshot = magnitudeSnapshot;
        else if (magnitudeSnapshot.HasValue)
        {
            var previous = effect.MagnitudeSnapshot ?? definition.ValuePerStack;
            updateSource = definition.SnapshotRefresh == BattleStatusSnapshotRefresh.Replace || magnitudeSnapshot.Value >= previous;
            effect.MagnitudeSnapshot = definition.SnapshotRefresh == BattleStatusSnapshotRefresh.Replace
                ? magnitudeSnapshot.Value : Math.Max(previous, magnitudeSnapshot.Value);
        }
        effect.Lifetime = definition.Lifetime == BattleStatusLifetime.Rounds && durationRounds == 0
            ? BattleStatusLifetime.CurrentRound : definition.Lifetime;
        var expiry = effect.Lifetime switch
        {
            BattleStatusLifetime.CurrentRound => room.RoundNumber,
            BattleStatusLifetime.Rounds => checked(room.RoundNumber + durationRounds),
            _ => int.MaxValue
        };
        effect.ExpiresAfterRound = active && definition.EffectType is "DamageOverTime" or "HealOverTime"
            ? Math.Max(effect.ExpiresAfterRound, expiry) : expiry;
        if (updateSource)
        {
            effect.SourceActorType = source?.ActorType;
            effect.SourceActorId = source?.ActorId;
            effect.SourceSkillCode = source?.SkillCode;
        }
        effect.BoundTargetType = boundTargetType;
        effect.BoundTargetId = boundTargetId;
        CaptureStatus(room, effect, active ? BattleStatusChange.Refreshed : BattleStatusChange.Added, countBefore, effect.Stacks);
        logs.Add($"{targetLabel} 获得 {definition.Name}，持续 {durationRounds} 回合{(effect.Stacks > 1 ? $"（{effect.Stacks} 层）" : "")}。");
        return true;
    }

    public async Task<BattleStatusEffect> SetCounterAsync(Room room, string targetType, int targetId,
        string code, int count, BattleStatusSource? source = null,
        string? boundTargetType = null, int? boundTargetId = null)
    {
        await PrepareAsync(room);
        var definition = CatalogFor(room).Find(code) ?? throw new InvalidOperationException($"Missing battle status: {code}");
        if (count < 1 || count > definition.MaxStacks) throw new ArgumentOutOfRangeException(nameof(count));
        await ApplyAsync(room, targetType, targetId, code, 0, [], string.Empty,
            source: source, boundTargetType: boundTargetType, boundTargetId: boundTargetId, counterCount: count);
        var effect = (await GetActiveAsync(room, targetType, [targetId])).Single(entry => entry.EffectCode == code);
        return effect;
    }

    public async Task<int> ConsumeAsync(Room room, string targetType, int targetId, string code, int count = int.MaxValue)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        var effect = (await GetActiveAsync(room, targetType, [targetId])).FirstOrDefault(entry => entry.EffectCode == code);
        if (effect is null) return 0;
        var before = effect.Stacks;
        var consumed = Math.Min(count, before);
        if (consumed == effect.Stacks) db.BattleStatusEffects.Remove(effect);
        else effect.Stacks -= consumed;
        CaptureStatus(room, effect, BattleStatusChange.Consumed, before, before - consumed);
        return consumed;
    }

    public async Task<int> AmplifyDamageAsync(Room room, int monsterId, int damage)
    {
        if (damage <= 0) return damage;
        var amplification = await MechanicPowerAsync(room, "Monster", monsterId, BattleStatusMechanic.HunterVulnerability);
        return amplification == 0 ? damage : (int)Math.Min(int.MaxValue,
            decimal.Floor(damage * (1m + amplification / 100m)));
    }

    public async Task ResolveEndOfRoundAsync(Room room, Monster monster,
        IReadOnlyList<BattleParticipant> participants, List<string> logs,
        IReadOnlyDictionary<int, OperationPotionBonuses>? operationBonuses = null,
        bool healingOnly = false)
    {
        await PrepareAsync(room);
        var effects = SettlementStates(room)?.ToList() ?? await db.BattleStatusEffects.Where(effect =>
            effect.RoomId == room.Id && effect.RunSequence == room.RunSequence).ToListAsync();
        effects.RemoveAll(effect => db.Entry(effect).State == EntityState.Deleted);
        foreach (var local in db.BattleStatusEffects.Local.Where(effect => effect.RoomId == room.Id &&
                     effect.RunSequence == room.RunSequence && db.Entry(effect).State != EntityState.Deleted))
            if (!effects.Contains(local)) effects.Add(local);
        foreach (var effect in effects.Where(effect => effect.AppliedRound < room.RoundNumber &&
                     effect.ExpiresAfterRound >= room.RoundNumber))
        {
            var definition = CatalogFor(room).Find(effect.EffectCode);
            if (healingOnly && definition?.EffectType != "HealOverTime") continue;
            if (definition?.EffectType == "HealOverTime")
            {
                BattleActor? target = effect.TargetType == "Monster" && effect.TargetId == monster.Id
                    ? BattleActor.ForMonster(monster)
                    : effect.TargetType == "Character" && participants.SingleOrDefault(entry => entry.Character.Id == effect.TargetId) is { } participant
                        ? BattleActor.ForCharacter(participant) : null;
                if (target is null || target.Hp <= 0) continue;
                var healing = Math.Max(1, effect.PerTickValue ??
                    (int)decimal.Floor(Math.Abs(definition.ValuePerStack) * effect.Stacks));
                var hpBefore = target.Hp;
                var restored = BattleDamageService.RestoreHp(target, healing);
                using var healingAction = Events.ActionScope(Source(effect), definition.Name, BattleActionKind.Periodic);
                Events.Hp(room, BattleEventKind.Heal, null, target, healing, restored, hpBefore);
                if (restored <= 0) continue;
                logs.Add($"{target.Label} 受到 {definition.Name} 治疗，恢复 {restored} 点生命。");
                continue;
            }
            if (definition?.EffectType != "DamageOverTime") continue;
            var damage = Math.Max(1, effect.PerTickValue ??
                (int)decimal.Floor(Math.Abs(definition.ValuePerStack) * effect.Stacks));
            if (effect.TargetType == "Character")
            {
                var target = participants.SingleOrDefault(entry => entry.Character.Id == effect.TargetId);
                if (target is null || target.Character.Hp <= 0) continue;
                var taken = operationBonuses?.GetValueOrDefault(target.Character.Id).DamageTakenPercent ?? 0;
                damage = Math.Max(1, (int)decimal.Floor(damage * (1m + taken / 100m)));
                var hpBefore = target.Character.Hp;
                target.Character.Hp = Math.Max(0, target.Character.Hp - damage);
                using var damageAction = Events.ActionScope(Source(effect), definition.Name, BattleActionKind.Periodic);
                Events.Hp(room, BattleEventKind.Damage, null, BattleActor.ForCharacter(target), damage, hpBefore - target.Character.Hp, hpBefore);
                logs.Add($"{target.Slot.SlotIndex}号位 {target.Character.Name} 受到 {definition.Name} 造成的 {damage} 点伤害。");
            }
            else if (effect.TargetType == "Monster" && effect.TargetId == monster.Id && monster.Hp > 0)
            {
                damage = await AmplifyDamageAsync(room, monster.Id, damage);
                var hpBefore = monster.Hp;
                monster.Hp = Math.Max(0, monster.Hp - damage);
                using var damageAction = Events.ActionScope(Source(effect), definition.Name, BattleActionKind.Periodic);
                Events.Hp(room, BattleEventKind.Damage, null, BattleActor.ForMonster(monster), damage, hpBefore - monster.Hp, hpBefore);
                logs.Add($"{monster.Name} 受到 {definition.Name} 造成的 {damage} 点伤害。");
            }
        }

        RemoveStates(room, effects.Where(effect => effect.ExpiresAfterRound <= room.RoundNumber), BattleStatusChange.Expired);
    }

    public async Task ClearRunAsync(Room room)
    {
        await PrepareAsync(room);
        var effects = SettlementStates(room)?.ToList() ?? await db.BattleStatusEffects.Where(effect => effect.RoomId == room.Id && effect.RunSequence == room.RunSequence).ToListAsync();
        effects.AddRange(db.BattleStatusEffects.Local.Where(effect => effect.RoomId == room.Id && effect.RunSequence == room.RunSequence && !effects.Contains(effect)));
        RemoveStates(room, effects, BattleStatusChange.Removed);
    }

    public async Task RemoveBoundToAsync(int roomId, string actorType, int actorId)
    {
        var effects = await db.BattleStatusEffects.Where(effect => effect.RoomId == roomId &&
            (effect.TargetType == actorType && effect.TargetId == actorId ||
             effect.BoundTargetType == actorType && effect.BoundTargetId == actorId)).ToListAsync();
        effects.AddRange(db.BattleStatusEffects.Local.Where(effect => effect.RoomId == roomId &&
            (effect.TargetType == actorType && effect.TargetId == actorId ||
             effect.BoundTargetType == actorType && effect.BoundTargetId == actorId) && !effects.Contains(effect)));
        foreach (var effect in effects.Where(effect => db.Entry(effect).State != EntityState.Deleted))
        {
            var room = db.Rooms.Local.FirstOrDefault(entry => entry.Id == roomId);
            if (room is not null)
            {
                await PrepareAsync(room);
                CaptureStatus(room, effect, BattleStatusChange.Removed, effect.Stacks, 0);
            }
            db.BattleStatusEffects.Remove(effect);
        }
    }

    public async Task<List<BattleStatusEffectResponse>> DescribeAsync(Room room, string targetType, int targetId)
    {
        var responses = await DescribeManyAsync(room, targetType, [targetId]);
        return responses.GetValueOrDefault(targetId) ?? [];
    }

    public async Task<Dictionary<int, List<BattleStatusEffectResponse>>> DescribeManyAsync(Room room, string targetType,
        IReadOnlyCollection<int> targetIds)
    {
        var effects = await GetActiveAsync(room, targetType, targetIds);
        var monsterIds = effects.Where(effect => effect.BoundTargetType == "Monster" && effect.BoundTargetId.HasValue)
            .Select(effect => effect.BoundTargetId!.Value).Distinct().ToList();
        var names = ReadSnapshot(room)?.MonsterNames ?? (monsterIds.Count == 0 ? new Dictionary<int, string>() :
            await db.Monsters.Where(monster => monsterIds.Contains(monster.Id))
                .Select(monster => new { monster.Id, monster.Name })
                .ToDictionaryAsync(monster => monster.Id, monster => monster.Name));
        var byTarget = effects.ToLookup(effect => effect.TargetId);
        return targetIds.Distinct().ToDictionary(id => id, id => byTarget[id].OrderBy(effect => effect.Id)
            .Select(effect => Snapshot(room, effect, room.RoundNumber,
                effect.BoundTargetType == "Monster" && effect.BoundTargetId is int boundId ? names.GetValueOrDefault(boundId) : null)?.ToResponse())
            .OfType<BattleStatusEffectResponse>().ToList());
    }

    private static BattleStatusSource? Source(BattleStatusEffect effect) => effect.SourceActorType is { } type && effect.SourceActorId is { } id
        ? new(type, id, effect.SourceSkillCode) : null;

    private void RemoveStates(Room room, IEnumerable<BattleStatusEffect> effects, BattleStatusChange reason)
    {
        foreach (var effect in effects.Where(effect => db.Entry(effect).State != EntityState.Deleted).ToList())
        {
            CaptureStatus(room, effect, reason, effect.Stacks, 0);
            db.BattleStatusEffects.Remove(effect);
        }
    }

    private void CaptureStatus(Room room, BattleStatusEffect effect, BattleStatusChange change, int before, int after,
        BattleEventKind kind = BattleEventKind.Status)
    {
        if (Snapshot(room, effect, room.RoundNumber, Events.ActorName(effect.BoundTargetType, effect.BoundTargetId)) is { } snapshot)
            Events.Status(room, effect.TargetType, effect.TargetId, snapshot, change, before, after, kind);
    }

    private BattleStatusSnapshot? Snapshot(Room room, BattleStatusEffect effect, int round, string? boundName = null)
    {
        var definition = CatalogFor(room).Find(effect.EffectCode);
        if (definition is null) return null;
        var rounds = effect.Lifetime == BattleStatusLifetime.Rounds
            ? (int)Math.Clamp((long)effect.ExpiresAfterRound - round + 1, 0, int.MaxValue) : 0;
        var duration = effect.Lifetime switch
        {
            BattleStatusLifetime.UntilConsumed => "消耗后移除，挑战结束时清空",
            BattleStatusLifetime.Encounter => "当前目标死亡或挑战结束时清空",
            BattleStatusLifetime.Run => "本次挑战持续",
            BattleStatusLifetime.CurrentRound => "本回合有效",
            _ => $"剩余 {rounds} 回合"
        };
        if (effect.BoundTargetId.HasValue && effect.Lifetime == BattleStatusLifetime.Rounds)
            duration += "，绑定目标死亡时清空";
        return new()
        {
            Code = effect.EffectCode, Name = definition.Name, EffectType = definition.EffectType,
            Description = definition.Mechanic switch
            {
                BattleStatusMechanic.Guard when effect.MagnitudeSnapshot.HasValue => $"本回合受到的直接伤害降低 {effect.MagnitudeSnapshot:0.##}%。",
                BattleStatusMechanic.NormalAttackEcho => $"下次普通攻击附加 {effect.MagnitudeSnapshot ?? definition.FamilyStrength:0.##}% 追击；消费后移除。",
                _ => _mechanicDescriptions.Status(definition, effect.MagnitudeSnapshot)
            },
            IsPositive = definition.IsPositive, CanDispel = definition.IsDispellable, Stacks = effect.Stacks,
            RemainingRounds = rounds, Lifetime = effect.Lifetime, CounterKind = definition.CounterKind, Mechanic = definition.Mechanic,
            AppliedRound = effect.AppliedRound, ExpiresAfterRound = effect.ExpiresAfterRound, PerTickValue = effect.PerTickValue,
            MagnitudeSnapshot = effect.MagnitudeSnapshot, DurationText = duration,
            CounterText = definition.CounterKind switch
            {
                BattleStatusCounterKind.Charges => $"剩余 {effect.Stacks} 次",
                BattleStatusCounterKind.Stacks => $"{effect.Stacks} 层",
                _ => string.Empty
            },
            SourceActorType = effect.SourceActorType, SourceActorId = effect.SourceActorId, SourceSkillCode = effect.SourceSkillCode,
            BoundTargetType = effect.BoundTargetType, BoundTargetId = effect.BoundTargetId, BoundTargetName = boundName
        };
    }
}

public sealed record RemovedBattleStatus(int TargetId, string Code, string Name, bool IsPositive);
