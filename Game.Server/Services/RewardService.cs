using Game.Server.Data;
using Game.Server.Configuration;
using Game.Shared;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Game.Server.Services;

public sealed partial class RewardService(GameDbContext dbContext, RewardCatalog catalog, ProgressionService progression,
    ProductionService? production = null, PlantingCatalog? planting = null,
    DungeonDepthProgressService? depthProgress = null, DungeonRunRulesService? runRules = null)
{
    private readonly DungeonDepthProgressService _depthProgress = depthProgress ?? new(dbContext, runRules: runRules);
    private readonly CombatProfessionProgressStore _professionProgress = new(dbContext);

    public Task CaptureDungeonParticipantsAsync(Room room, IEnumerable<int> characterIds) =>
        _depthProgress.CaptureAsync(room, characterIds);
    public IReadOnlyList<RewardDropPreview> GetDropPreview(string rewardCode, bool isClear, int depthLevel = 1)
    {
        var drops = catalog.GetDropPreview(rewardCode, isClear).ToList();
        if (isClear && planting is not null)
            drops.AddRange(planting.SeedDropsFor(rewardCode, depthLevel)
                .Select(drop => new RewardDropPreview("Material", drop.SeedCode,
                    planting.FindSeed(drop.SeedCode)!.Name + "种子", 1, drop.DropChancePercent, null)));
        return drops;
    }

    public bool HasRewardProfile(string rewardCode, bool isClear) =>
        catalog.HasRewardProfile(rewardCode, isClear);

    public IReadOnlyList<RewardDropPreview> GetFirstSeedClearPreview(string dungeonCode) => planting is null ? [] :
        planting.SeedDropsFor(dungeonCode).Where(drop => drop.FirstClearGuaranteed)
            .Select(drop => new RewardDropPreview("Material", drop.SeedCode,
                planting.FindSeed(drop.SeedCode)!.Name + "种子", 1, 100, null)).ToList();

    public async Task<bool> HasRewardProfileAsync(Room room, string rewardCode, bool isClear)
    {
        if (runRules is null) return HasRewardProfile(rewardCode, isClear);
        var frozen = (await runRules.EnsureAsync(room)).Rewards;
        return (isClear ? frozen.Clears : frozen.Kills).ContainsKey(rewardCode);
    }

    public async Task<CoopDropBonusOptions> GetCoopDropBonusAsync(Room room) => runRules is null
        ? catalog.CoopDropBonus : (await runRules.EnsureAsync(room)).Rewards.CoopDropBonus;

    public async Task<bool> RecordAsync(Room room, string dungeonCode, IEnumerable<RewardParticipant> participants,
        string eventKey, bool isClear, IReadOnlyCollection<int>? actualParticipantCharacterIds = null)
    {
        var frozen = runRules is null ? null : (await runRules.EnsureAsync(room)).Rewards;
        var run = await GetRunAsync(room);
        if (run.Status != "Pending" || dbContext.RewardEvents.Local.Any(entry =>
                entry.RoomId == room.Id && entry.Sequence == room.RunSequence && entry.EventKey == eventKey) ||
            await dbContext.RewardEvents.AnyAsync(entry => entry.RoomId == room.Id &&
                entry.Sequence == room.RunSequence && entry.EventKey == eventKey)) return false;

        var recipients = participants.DistinctBy(entry => entry.Character.Id).ToList();
        var isRegular = eventKey == "clear" || eventKey.StartsWith("monster:", StringComparison.Ordinal);
        var actualIds = actualParticipantCharacterIds?.ToHashSet() ?? [];
        var eligibleCoopRecipients = isRegular ? recipients.Where(participant => actualIds.Contains(participant.Character.Id) &&
            participant.UserId > 0 && participant.UserId == participant.Character.UserId).ToList() : [];
        var coopCharacterIds = eligibleCoopRecipients.Select(participant => participant.Character.Id).ToHashSet();
        var coopUserCount = eligibleCoopRecipients.Select(participant => participant.Character.UserId).Distinct().Count();
        var coopPolicy = frozen?.CoopDropBonus ?? catalog.CoopDropBonus;
        var coopBonus = CoopDropBonusRules.CalculateBonusPercent(coopUserCount,
            coopPolicy.PercentPerAdditionalUser, coopPolicy.MaximumPercent);
        dbContext.RewardEvents.Add(new RewardEvent
        {
            RoomId = room.Id, Sequence = room.RunSequence, EventKey = eventKey,
            CoopParticipantCount = coopUserCount, CoopDropBonusPercent = coopBonus
        });
        var definition = await _depthProgress.DefinitionAsync(room);
        foreach (var participant in recipients)
        {
            var entries = catalog.Roll(dungeonCode, isClear, room.Id, room.RunSequence,
                eventKey, participant.UserId, participant.Character.Id, frozen,
                coopCharacterIds.Contains(participant.Character.Id) ? coopBonus : 0).ToList();
            if (definition is not null && isRegular)
            {
                var mastery = await _depthProgress.RunMasteryAsync(room, participant.Character.Id);
                if (mastery >= 2)
                {
                    // Carry fractional gold across kills in this attempt rather than losing it on each small drop.
                    var savedGold = await dbContext.RewardEntries.Where(item => item.RoomId == room.Id &&
                        item.Sequence == room.RunSequence && item.CharacterId == participant.Character.Id &&
                        item.Kind == "Gold" && item.RewardSource == "Base" &&
                        (item.EventKey == "clear" || item.EventKey.StartsWith("monster:"))).ToListAsync();
                    var previousGold = savedGold.Concat(dbContext.RewardEntries.Local.Where(item =>
                        item.RoomId == room.Id && item.Sequence == room.RunSequence &&
                        item.CharacterId == participant.Character.Id && item.Kind == "Gold" && item.RewardSource == "Base" &&
                        (item.EventKey == "clear" || item.EventKey.StartsWith("monster:", StringComparison.Ordinal))))
                        .Distinct().Sum(item => (long)item.Quantity);
                    var baseGold = entries.Where(item => item.Kind == "Gold").Sum(item => (long)item.Quantity);
                    var rate = definition.GoldBonusPercent / 100m;
                    var bonus = checked((int)(decimal.Floor((previousGold + baseGold) * rate) - decimal.Floor(previousGold * rate)));
                    if (bonus > 0) entries.Add(new RewardEntry
                    {
                        RoomId = room.Id, Sequence = room.RunSequence, EventKey = eventKey,
                        UserId = participant.UserId, CharacterId = participant.Character.Id,
                        Kind = "Gold", Quantity = bonus, RewardSource = "Mastery"
                    });
                }
                var chance = isClear ? mastery >= 4 ? definition.ClearExtraRollChancePercent : 0m
                    : mastery >= 3 ? definition.KillExtraRollChancePercent : 0m;
                if (catalog.RollChance(chance))
                {
                    var extra = catalog.Roll(dungeonCode, isClear, room.Id, room.RunSequence,
                        eventKey, participant.UserId, participant.Character.Id, frozen)
                        .Where(item => item.Kind is not ("Gold" or "Experience")).ToList();
                    foreach (var item in extra) item.RewardSource = "Mastery";
                    entries.AddRange(extra);
                }
                if (isClear && room.DepthLevel >= definition.ChallengeStartDepth &&
                    catalog.RollChance(definition.ChallengeFragmentChancePercent))
                    entries.Add(new RewardEntry
                    {
                        RoomId = room.Id, Sequence = room.RunSequence, EventKey = eventKey,
                        UserId = participant.UserId, CharacterId = participant.Character.Id,
                        Kind = "Material", Code = definition.ChallengeFragmentCode,
                        Quantity = definition.ChallengeFragmentsAt(room.DepthLevel), RewardSource = "Challenge"
                    });
            }
            dbContext.RewardEntries.AddRange(entries);
        }
        return true;
    }

    public async Task SettleAsync(Room room, bool victory, DateTime now, List<string> logs)
    {
        var run = await GetRunAsync(room);
        if (run.Status != "Pending") return;
        var saved = await dbContext.RewardEntries.Where(entry => entry.RoomId == room.Id &&
            entry.Sequence == room.RunSequence).ToListAsync();
        var pending = dbContext.ChangeTracker.Entries<RewardEntry>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.RoomId == room.Id &&
                entry.Entity.Sequence == room.RunSequence).Select(entry => entry.Entity);
        var entries = saved.Concat(pending).ToList();
        var characterIds = entries.Select(entry => entry.CharacterId).Distinct().ToList();
        if (production is not null)
            foreach (var characterId in characterIds)
                await production.SettleCharacterTrackedAsync(characterId, now);
        var characters = await dbContext.Characters.Where(character => characterIds.Contains(character.Id))
            .ToDictionaryAsync(character => character.Id);
        var stacks = (await dbContext.CharacterItemStacks.Where(stack => characterIds.Contains(stack.CharacterId)).ToListAsync())
            .Concat(dbContext.CharacterItemStacks.Local.Where(stack => characterIds.Contains(stack.CharacterId)))
            .DistinctBy(stack => (stack.CharacterId, stack.ItemCode)).ToList();

        var dungeon = await dbContext.Dungeons.FindAsync(room.DungeonId)
            ?? throw new InvalidOperationException($"Dungeon {room.DungeonId} was not found while settling rewards.");
        var ruleDefinition = runRules is null ? null : await runRules.EnsureAsync(room);
        var frozen = ruleDefinition?.Rewards;
        var tutorial = victory && (ruleDefinition?.DungeonKind ?? dungeon.DungeonKind) == "Hunt"
            ? frozen is null ? catalog.FirstHuntWeapon(dungeon.Code) : frozen.FirstHuntWeapon : null;
        if (tutorial is not null)
        {
            const string eventKey = "starter-hunt-weapon";
            var claimedIds = (await dbContext.CharacterFirstHuntWeaponClaims
                    .Where(claim => claim.DungeonId == dungeon.Id && characterIds.Contains(claim.CharacterId))
                    .Select(claim => claim.CharacterId).ToListAsync())
                .Concat(dbContext.CharacterFirstHuntWeaponClaims.Local
                    .Where(claim => claim.DungeonId == dungeon.Id).Select(claim => claim.CharacterId))
                .ToHashSet();
            var recipients = entries.GroupBy(entry => entry.CharacterId)
                .Where(group => !claimedIds.Contains(group.Key))
                .Select(group => (UserId: group.First().UserId, CharacterId: group.Key)).ToList();
            if (recipients.Count > 0)
                dbContext.RewardEvents.Add(new RewardEvent { RoomId = room.Id, Sequence = room.RunSequence, EventKey = eventKey });
            foreach (var recipient in recipients)
            {
                dbContext.CharacterFirstHuntWeaponClaims.Add(new CharacterFirstHuntWeaponClaim
                    { CharacterId = recipient.CharacterId, DungeonId = dungeon.Id });
                var entry = new RewardEntry { RoomId = room.Id, Sequence = room.RunSequence, EventKey = eventKey,
                    UserId = recipient.UserId, CharacterId = recipient.CharacterId, Kind = "Weapon", Code = tutorial.Code,
                    Quantity = 1, WeaponSnapshotJson = JsonSerializer.Serialize(tutorial) };
                entries.Add(entry);
                dbContext.RewardEntries.Add(entry);
                logs.Add($"{characters[recipient.CharacterId].Name} 首次通关此地区 Lv1 讨伐，获得新手武器奖励。");
            }
        }

        foreach (var group in entries.GroupBy(entry => entry.CharacterId).ToList())
        {
            var experienceEntries = group.Where(entry => entry.Kind == "Experience").ToList();
            if (experienceEntries.Count == 0) continue;
            var baseExperience = experienceEntries.Sum(entry => entry.Quantity);
            var adjustedExperience = progression.GetAwardableExperience(
                baseExperience, characters[group.Key].Level);
            if (adjustedExperience == baseExperience) continue;

            for (var index = 0; index < experienceEntries.Count; index++)
            {
                var entry = experienceEntries[index];
                if (index == 0 && adjustedExperience > 0)
                {
                    entry.Quantity = adjustedExperience;
                    continue;
                }
                entries.Remove(entry);
                dbContext.RewardEntries.Remove(entry);
            }
        }

        foreach (var group in entries.GroupBy(entry => entry.CharacterId))
        {
            var character = characters[group.Key];
            var gold = group.Where(entry => entry.Kind == "Gold").Sum(entry => entry.Quantity);
            if (gold > 0)
            {
                character.Gold = checked(character.Gold + gold);
                character.Version++;
                logs.Add($"{character.Name} 获得 {gold} 金币。");
            }
            var experience = group.Where(entry => entry.Kind == "Experience").Sum(entry => entry.Quantity);
            if (experience > 0)
            {
                var gain = await _professionProgress.AwardExperienceAsync(character, experience, progression);
                if (gain.ExperienceGained > 0) logs.Add($"{character.Name} 获得 {gain.ExperienceGained} 点经验值。");
                if (gain.LevelsGained > 0) logs.Add($"{character.Name} 的当前职业升至 Lv.{character.Level}。");
            }
            foreach (var items in group.Where(entry => entry.Kind is "Consumable" or "Material").GroupBy(entry => entry.Code))
            {
                var stack = stacks.SingleOrDefault(item => item.CharacterId == character.Id && item.ItemCode == items.Key);
                if (stack is null)
                {
                    stack = new CharacterItemStack { CharacterId = character.Id, ItemCode = items.Key };
                    stacks.Add(stack);
                    dbContext.CharacterItemStacks.Add(stack);
                }
                else stack.Version++;
                var quantity = items.Sum(entry => entry.Quantity);
                stack.Quantity = checked(stack.Quantity + quantity);
                logs.Add($"{character.Name} 获得 {catalog.Describe(items.First())} × {quantity}。");
            }
            foreach (var weapon in group.Where(entry => entry.Kind == "Weapon"))
            {
                var snapshot = RewardCatalog.DeserializeWeapon(weapon)
                    ?? throw new InvalidOperationException("Missing weapon reward snapshot.");
                var displayName = snapshot.DisplayName;
                for (var i = 0; i < weapon.Quantity; i++)
                {
                    var awarded = catalog.MaterializeWeapon(snapshot, character.Id);
                    dbContext.CharacterWeapons.Add(awarded);
                    displayName = (snapshot with { Name = awarded.Name }).DisplayName;
                }
                logs.Add($"{character.Name} 获得 {displayName} × {weapon.Quantity}。");
            }
            foreach (var soulImprint in group.Where(entry => entry.Kind == "SoulImprint"))
            {
                for (var i = 0; i < soulImprint.Quantity; i++)
                    dbContext.CharacterSoulImprints.Add(catalog.MaterializeSoulImprint(soulImprint.Code, character.Id));
                logs.Add($"{character.Name} 获得魂印「{catalog.Describe(soulImprint)}」× {soulImprint.Quantity}。");
            }
        }
        run.Status = victory ? "Victory" : "Defeat";
        run.SettledAtUtc = now;
        logs.Add(victory ? "副本奖励结算完毕。" : "战败，已结算本次战斗中获得的击杀奖励。");
    }

    private async Task<RewardRun> GetRunAsync(Room room)
    {
        var run = dbContext.RewardRuns.Local.FirstOrDefault(entry => entry.RoomId == room.Id && entry.Sequence == room.RunSequence)
            ?? await dbContext.RewardRuns.FindAsync(room.Id, room.RunSequence);
        if (run is not null) return run;
        run = new RewardRun { RoomId = room.Id, Sequence = room.RunSequence };
        dbContext.RewardRuns.Add(run);
        return run;
    }
}

public sealed record RewardParticipant(int UserId, Character Character);
