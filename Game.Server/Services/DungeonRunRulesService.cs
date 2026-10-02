using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

/// <summary>
/// Freezes dungeon rules for a room, including repeated attempts. New rooms use new content.
/// Capturing and restoring only stage tracked changes; the caller owns the atomic save.
/// </summary>
public sealed class DungeonRunRulesService(GameDbContext db, MonsterCombatCatalog combat,
    RewardCatalog rewards, PartyScalingCatalog parties, DungeonDepthCatalog depths, PlantingCatalog? plants = null,
    DungeonEncounterCatalog? encounters = null, IOptions<CombatDamageOptions>? damageOptions = null)
{
    private readonly decimal _damageVariancePercent = DamageVariance.ValidatePercent(damageOptions?.Value.VariancePercent ?? 0);
    private readonly Dictionary<int, (DungeonRunRuleSnapshot Row, DungeonRunDefinition Definition, MonsterCombatCatalog Combat)> _loaded = [];

    public async Task<DungeonRunDefinition> EnsureAsync(Room room)
    {
        if (_loaded.TryGetValue(room.Id, out var cached) && db.Entry(cached.Row).State != EntityState.Detached)
            return cached.Definition;
        var row = await db.DungeonRunRuleSnapshots.FindAsync(room.Id);
        if (row is null)
        {
            var dungeon = await db.Dungeons.FindAsync(room.DungeonId)
                ?? throw new InvalidOperationException($"Missing dungeon for room {room.Id}.");
            var monsters = await MonstersAsync(room);
            if (monsters.Count == 0) throw new InvalidOperationException($"Missing encounter for room {room.Id}.");
            var depth = room.DepthDefinitionJson is not null
                ? JsonSerializer.Deserialize<DungeonDepthDefinitionOptions>(room.DepthDefinitionJson)
                : depths.Find(dungeon.Code);
            var definition = new DungeonRunDefinition
            {
                DungeonCode = dungeon.Code, DungeonKind = dungeon.DungeonKind, DepthLevel = room.DepthLevel,
                DirectDamageVariancePercent = _damageVariancePercent,
                Depth = depth,
                RewardEligibility = encounters?.ResolveRewardEligibility(dungeon.Code,
                    depth is null ? DungeonRewardEligibility.CurrentSlots : DungeonRewardEligibility.ActualParticipants)
                    ?? (depth is null ? DungeonRewardEligibility.CurrentSlots : DungeonRewardEligibility.ActualParticipants),
                PartyHpPercentages = parties.GetHpPercentages(dungeon.PartyScalingProfileCode).ToArray(),
                Monsters = monsters.Select(DungeonMonsterDefinition.Capture).ToList(),
                Combat = combat.ExportOptions(),
                Rewards = rewards.CaptureRules(dungeon.Code, monsters.Select(monster =>
                    string.IsNullOrWhiteSpace(monster.RewardProfileCode) ? dungeon.Code : monster.RewardProfileCode)),
                RareSeeds = plants?.Plants.Where(plant => plant.IsRare &&
                        (plant.UnlockTargetCode == dungeon.Code || plant.AlternativeUnlockTargetCodes.Contains(dungeon.Code)))
                    .Select(plant => new DungeonSeedDrop(plant.SeedCode, plant.DropChancePercent)).ToList() ?? []
            };
            // Round-trip detaches all mutable configuration collections from singleton catalogs.
            var json = JsonSerializer.Serialize(definition);
            row = new() { RoomId = room.Id, DefinitionJson = json, Revision = Hash(json) };
            db.DungeonRunRuleSnapshots.Add(row);
            // Also freezes the explicit absence of depth rules in the snapshot.
            room.DepthDefinitionJson = depth is null ? null : JsonSerializer.Serialize(depth);
        }
        if (row.Revision != Hash(row.DefinitionJson)) throw new InvalidOperationException("Dungeon rule snapshot checksum mismatch.");
        var loaded = JsonSerializer.Deserialize<DungeonRunDefinition>(row.DefinitionJson)
            ?? throw new InvalidOperationException("Empty dungeon rule snapshot.");
        if (loaded.SchemaVersion != 1 || loaded.DepthLevel != room.DepthLevel || loaded.PartyHpPercentages.Length != 5 ||
            !Enum.IsDefined(loaded.RewardEligibility) || loaded.DirectDamageVariancePercent is < 0 or > 100)
            throw new InvalidOperationException("Unsupported dungeon rule snapshot.");
        loaded.Revision = row.Revision;
        loaded.Rewards.Kills = new(loaded.Rewards.Kills, StringComparer.OrdinalIgnoreCase);
        loaded.Rewards.Clears = new(loaded.Rewards.Clears, StringComparer.OrdinalIgnoreCase);
        loaded.Rewards.Weapons = new(loaded.Rewards.Weapons, StringComparer.OrdinalIgnoreCase);
        var frozenCombat = new MonsterCombatCatalog(Options.Create(loaded.Combat));
        _loaded[room.Id] = (row, loaded, frozenCombat);
        return loaded;
    }

    public MonsterCombatCatalog CombatFor(Room room) => _loaded.TryGetValue(room.Id, out var cached) &&
        db.Entry(cached.Row).State != EntityState.Detached ? cached.Combat :
        throw new InvalidOperationException("Load dungeon rules before resolving battle definitions.");

    public async Task<DungeonRunDefinition> RefreshAsync(Room room)
    {
        var definition = await EnsureAsync(room);
        var monsters = await MonstersAsync(room);
        foreach (var monster in monsters)
            definition.Monsters.Single(item => item.WaveNumber == monster.WaveNumber && item.Position == monster.Position).Restore(monster);
        var first = monsters.First();
        room.MonsterId = first.Id;
        room.CurrentWaveNumber = first.WaveNumber;
        room.TotalWaveCount = monsters.Max(monster => monster.WaveNumber);
        room.ScalingPartySize = 1;
        return definition;
    }

    private async Task<List<Monster>> MonstersAsync(Room room)
    {
        var monsters = await db.Monsters.Where(monster => monster.RoomId == room.Id)
            .OrderBy(monster => monster.WaveNumber).ThenBy(monster => monster.Position).ToListAsync();
        if (monsters.Count == 0 && await db.Monsters.FindAsync(room.MonsterId) is { } legacy) monsters.Add(legacy);
        return monsters;
    }
    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
}
