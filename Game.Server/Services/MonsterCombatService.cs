using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class MonsterCombatService(GameDbContext dbContext, MonsterCombatCatalog catalog, Random? random = null,
    BattleStatusService? statuses = null, SkillCatalog? characterSkills = null, BattleEffectExecutor? battleEffects = null,
    SkillInformationService? skillInformation = null, ProfessionMechanicCatalog? mechanics = null, DungeonRunRulesService? runRules = null,
    MonsterPhaseService? phases = null)
{
    private readonly SkillInformationService _information = skillInformation ?? new(catalog.Statuses, mechanics);
    public BattleStatusService Statuses { get; } = statuses ?? new BattleStatusService(dbContext, catalog.Statuses, mechanics: mechanics, runRules: runRules);
    private MonsterPhaseService? _phases = phases;
    public MonsterPhaseService Phases => _phases ??= new(dbContext, catalog, Statuses, runRules);
    private BattleGuardService? _guards = battleEffects?.Guards;
    private BattleGuardService Guards => _guards ??= new(Statuses);
    private BattleEffectExecutor? _effects = battleEffects;
    private BattleEffectExecutor Effects => _effects ??= new(characterSkills ?? new SkillCatalog(Options.Create(new SkillOptions())),
        Statuses, Guards, new BattleDamageService(Statuses, Guards, random, runRules: runRules, phases: Phases));
    private MonsterCombatCatalog CatalogFor(Room room) => runRules?.CombatFor(room) ?? catalog;
    public async Task<MonsterIntent> EnsureIntentAsync(Room room, Monster monster)
    {
        if (runRules is not null) await runRules.EnsureAsync(room);
        var existing = dbContext.MonsterIntents.Local.FirstOrDefault(intent =>
                intent.RoomId == room.Id && intent.RunSequence == room.RunSequence &&
                intent.RoundNumber == room.RoundNumber && intent.MonsterId == monster.Id)
            ?? await dbContext.MonsterIntents.FirstOrDefaultAsync(intent =>
                intent.RoomId == room.Id && intent.RunSequence == room.RunSequence &&
                intent.RoundNumber == room.RoundNumber && intent.MonsterId == monster.Id);
        if (existing is not null)
        {
            if (existing.TargetType == "Front")
                existing.TargetCharacterId = await FindFrontCharacterIdAsync(room.Id);
            return existing;
        }

        var selectedSkill = await SelectSkillAsync(room, monster);
        var silenced = selectedSkill?.IsInterruptible == true &&
            (await GetActiveEffectsAsync(room, "Monster", [monster.Id])).Any(effect =>
                CatalogFor(room).FindStatus(effect.EffectCode)?.EffectType == "SilenceNextIntent" &&
                effect.AppliedRound < room.RoundNumber);
        var targetType = selectedSkill?.TargetType ?? "Front";
        var intent = new MonsterIntent
        {
            RoomId = room.Id,
            RunSequence = room.RunSequence,
            RoundNumber = room.RoundNumber,
            MonsterId = monster.Id,
            ActionType = selectedSkill is null ? "BasicAttack" : "Skill",
            SkillCode = selectedSkill?.Code,
            IsInterrupted = silenced,
            TargetType = targetType,
            TargetCharacterId = targetType == "Front" ? await FindFrontCharacterIdAsync(room.Id) : null,
            CreatedAtUtc = DateTime.UtcNow
        };
        dbContext.MonsterIntents.Add(intent);
        return intent;
    }

    public async Task<MonsterIntentResponse?> GetIntentResponseAsync(Room room, Monster monster)
    {
        if (room.Status == RoomStatus.BattleOver || monster.Hp <= 0) return null;
        var intent = await EnsureIntentAsync(room, monster);
        var skill = CatalogFor(room).ResolveSkill(intent.SkillCode);
        var targetLabel = intent.TargetType switch
        {
            "Self" => monster.Name,
            "AllAlive" => "全体角色",
            _ => await GetCharacterTargetLabelAsync(room.Id, intent.TargetCharacterId)
        };
        var linked = Phases.Definition(room, monster) is { } core && skill?.Code == core.LinkedSkillCode &&
            await Phases.WillBeHeatedAsync(room, monster);
        var linkedDescription = linked ? $"蓄热未解除时，另随机攻击{Phases.Definition(room, monster)!.ExtraTargetCount}名其他存活角色，各造成{Phases.Definition(room, monster)!.ExtraAttackPowerPercent}%攻击伤害。" : "";
        return new MonsterIntentResponse
        {
            ActionType = intent.ActionType,
            SkillCode = intent.SkillCode,
            ActionName = skill?.Name ?? "普通攻击",
            Description = (skill?.Description ?? "攻击预告中指定的前排角色。") + linkedDescription,
            TargetType = intent.TargetType,
            TargetCharacterId = intent.TargetCharacterId,
            TargetLabel = linked ? targetLabel + "及随机其他角色（蓄热未解除时）" : targetLabel,
            IsInterruptible = skill?.IsInterruptible == true,
            IsInterrupted = intent.IsInterrupted,
            DangerLevel = skill?.DangerLevel ?? "Normal",
            Effects = skill is null ? [] : (runRules is null ? _information : new SkillInformationService(CatalogFor(room).Statuses, mechanics)).Effects(skill)
        };
    }

    public async Task<bool> CanInterruptCurrentIntentAsync(Room room, Monster monster)
    {
        var intent = await EnsureIntentAsync(room, monster);
        return intent.ActionType == "Skill" && !intent.IsInterrupted &&
               CatalogFor(room).ResolveSkill(intent.SkillCode)?.IsInterruptible == true;
    }

    public bool HasAnyInterruptibleSkill(Monster monster, Room? room = null)
    {
        var definitions = room is null ? catalog : CatalogFor(room);
        var profile = definitions.ResolveProfile(monster.CombatProfileCode);
        return profile?.Skills.Any(entry => definitions.ResolveSkill(entry.Code) is { IsInterruptible: true } skill &&
            (profile.SkillUseChancePercent > 0 || skill.ForcedPriority > 0)) == true;
    }

    public async Task<bool> InterruptCurrentIntentAsync(Room room, Monster monster)
    {
        var intent = await EnsureIntentAsync(room, monster);
        if (intent.ActionType != "Skill" || intent.IsInterrupted ||
            CatalogFor(room).ResolveSkill(intent.SkillCode)?.IsInterruptible != true) return false;
        intent.IsInterrupted = true;
        return true;
    }

    public Task<bool> HasRemovableStatusAsync(Room room, string targetType,
        IReadOnlyCollection<int> targetIds, bool isPositive) => Statuses.HasRemovableAsync(room, targetType, targetIds, isPositive);

    public Task<bool> HasStatusAsync(Room room, string targetType, int targetId, string statusCode) =>
        Statuses.HasAsync(room, targetType, targetId, statusCode);

    public Task<int> GetStatusStacksAsync(Room room, string targetType, int targetId, string statusCode) =>
        Statuses.StacksAsync(room, targetType, targetId, statusCode);

    public Task RemoveStatusAsync(Room room, string targetType, int targetId, string statusCode) =>
        Statuses.RemoveAsync(room, targetType, targetId, statusCode);

    public Task<RemovedBattleStatus?> RemoveFirstStatusAsync(Room room, string targetType,
        IReadOnlyList<int> targetIds, bool isPositive) => Statuses.RemoveFirstAsync(room, targetType, targetIds, isPositive);

    public Task<bool> ApplyStatusAsync(Room room, string targetType, int targetId, string statusCode,
        int durationRounds, List<string> logs, string targetLabel, int? perTickValue = null,
        BattleStatusSource? source = null) => Statuses.ApplyAsync(room, targetType, targetId, statusCode,
            durationRounds, logs, targetLabel, perTickValue, source);

    public Task<List<BattleStatusEffectResponse>> GetStatusResponsesAsync(Room room, string targetType, int targetId) =>
        Statuses.DescribeAsync(room, targetType, targetId);

    public Task<Dictionary<int, List<BattleStatusEffectResponse>>> GetStatusResponsesAsync(Room room, string targetType,
        IReadOnlyCollection<int> targetIds) => Statuses.DescribeManyAsync(room, targetType, targetIds);

    public async Task ExecuteIntentAsync(Room room, Monster monster,
        IReadOnlyList<BattleParticipant> participants,
        IReadOnlyDictionary<int, ElementType> mainWeaponElements,
        List<string> logs,
        IReadOnlyDictionary<int, OperationPotionBonuses>? operationBonuses = null)
    {
        var battle = new BattleExecutionContext(room, monster,
            participants, mainWeaponElements,
            operationBonuses ?? new Dictionary<int, OperationPotionBonuses>(), logs);
        var intent = await EnsureIntentAsync(room, monster);
        dbContext.MonsterIntents.Remove(intent);
        var skill = intent.ActionType == "Skill" ? CatalogFor(room).ResolveSkill(intent.SkillCode) : null;
        if (intent.IsInterrupted)
        {
            if (skill is not null) await StartCooldownAsync(room, monster, skill);
            var wasSilenced = (await GetActiveEffectsAsync(room, "Monster", [monster.Id])).Any(effect =>
                CatalogFor(room).FindStatus(effect.EffectCode)?.EffectType == "SilenceNextIntent" &&
                effect.AppliedRound < room.RoundNumber);
            logs.Add(wasSilenced
                ? $"{monster.Name} 受到沉默影响，{skill?.Name ?? "行动"} 被自动打断，本回合行动取消。"
                : $"{monster.Name} 的 {skill?.Name ?? "行动"} 已被打断，本回合行动取消。");
            return;
        }
        if (skill is null)
        {
            var target = participants.SingleOrDefault(entry => entry.Character.Id == intent.TargetCharacterId && entry.Character.Hp > 0);
            if (target is null)
            {
                logs.Add($"{monster.Name} 的预定目标已经失效，本回合攻击取消。");
                return;
            }
            var basic = new MonsterSkillDefinition
            {
                Code = "basic-attack", Name = "普通攻击", Description = "普通攻击", CooldownRounds = 0, InitialCooldownRounds = 0,
                TargetType = "Front", DangerLevel = "Normal", IsInterruptible = false,
                Effects = [new(BattleEffectKind.Damage, BattleEffectTarget.ForMonster("Front"))]
            };
            await Effects.ExecuteAsync(new(battle, basic, battle.Enemy) { IntentTargetId = intent.TargetCharacterId, IsBasicAttack = true },
                new LegacyMonsterDefenseAdapter(dbContext, Guards));
            await KnightMechanics.ResolveCountersAsync(battle, Guards, Effects.Damage, mechanics);
            return;
        }

        var targets = skill.TargetType switch
        {
            "Self" => [],
            "AllAlive" => participants.Where(entry => entry.Character.Hp > 0).OrderBy(entry => entry.Slot.SlotIndex).ToList(),
            _ => participants.Where(entry => entry.Character.Id == intent.TargetCharacterId && entry.Character.Hp > 0).ToList()
        };
        if (skill.TargetType != "Self" && targets.Count == 0)
        {
            logs.Add($"{monster.Name} 的 {skill.Name} 失去有效目标，本回合行动取消。");
            return;
        }

        using var action = Statuses.Events.ActionScope(battle.Enemy, skill.Code, skill.Name, BattleActionKind.Skill);
        logs.Add($"{monster.Name} 使用 {skill.Name}。");
        var damageEffects = skill.Effects.Any(effect => effect.Kind == BattleEffectKind.Damage);
        var reduction = damageEffects && targets.Count > 0
            ? await Statuses.ConsumeMechanicPowerAsync(room, "Monster", monster.Id, BattleStatusMechanic.NextDamageSkillReduction) : 0;
        if (reduction > 0) logs.Add($"{monster.Name} 的 {skill.Name} 受到奥术扰乱，直接伤害降低 {reduction}%。");
        await Effects.ExecuteAsync(new(battle, skill, battle.Enemy)
        {
            IntentTargetId = intent.TargetCharacterId, MonsterSkillReduction = reduction
        }, new LegacyMonsterDefenseAdapter(dbContext, Guards));
        if (Phases.Definition(room, monster) is { } core && skill.Code == core.LinkedSkillCode &&
            monster.Hp > 0 && await Phases.IsHeatedAsync(room, monster))
        {
            var candidates = participants.Where(entry => entry.Character.Hp > 0 && entry.Character.Id != intent.TargetCharacterId)
                .OrderBy(entry => entry.Slot.SlotIndex).ToList();
            var splash = new MonsterSkillDefinition
            {
                Code = "fire-core-splash", Name = "熔火扩散", Description = "蓄热期间，火焰斩波及其他角色。",
                CooldownRounds = 0, InitialCooldownRounds = 0,
                TargetType = "Front", DangerLevel = "Dangerous", IsInterruptible = skill.IsInterruptible,
                Effects = [new(BattleEffectKind.Damage, BattleEffectTarget.ForMonster("Front"), AttackPowerPercent: core.ExtraAttackPowerPercent)]
            };
            for (var hit = 0; hit < core.ExtraTargetCount && candidates.Count > 0; hit++)
            {
                var index = (random ?? Random.Shared).Next(candidates.Count);
                var target = candidates[index];
                candidates.RemoveAt(index);
                await Effects.ExecuteAsync(new(battle, splash, battle.Enemy)
                {
                    IntentTargetId = target.Character.Id, MonsterSkillReduction = reduction
                }, new LegacyMonsterDefenseAdapter(dbContext, Guards));
                await Phases.RecordLinkedHitAsync(room, monster);
            }
        }
        await StartCooldownAsync(room, monster, skill);
        await KnightMechanics.ResolveCountersAsync(battle, Guards, Effects.Damage, mechanics);
    }

    private async Task StartCooldownAsync(Room room, Monster monster, MonsterSkillDefinition skill)
    {
        var cooldown = dbContext.BattleMonsterSkillCooldowns.Local.FirstOrDefault(entry =>
                entry.RoomId == room.Id && entry.MonsterId == monster.Id && entry.SkillCode == skill.Code)
            ?? await dbContext.BattleMonsterSkillCooldowns.SingleOrDefaultAsync(entry =>
                entry.RoomId == room.Id && entry.MonsterId == monster.Id && entry.SkillCode == skill.Code);
        if (cooldown is null)
        {
            cooldown = new BattleMonsterSkillCooldown
            {
                RoomId = room.Id,
                MonsterId = monster.Id,
                SkillCode = skill.Code
            };
            dbContext.BattleMonsterSkillCooldowns.Add(cooldown);
        }
        cooldown.ReadyAtRound = checked(room.RoundNumber + skill.CooldownRounds + 1);
    }

    public Task ResolveEndOfRoundAsync(Room room, Monster monster,
        IReadOnlyList<BattleParticipant> participants, List<string> logs,
        IReadOnlyDictionary<int, OperationPotionBonuses>? operationBonuses = null,
        bool healingOnly = false) => Statuses.ResolveEndOfRoundAsync(room, monster, participants,
            logs, operationBonuses, healingOnly);

    public async Task ResetRoomStateAsync(int roomId)
    {
        await Phases.ResetRoomAsync(roomId);
        dbContext.MonsterIntents.RemoveRange(await dbContext.MonsterIntents.Where(entry => entry.RoomId == roomId).ToListAsync());
        dbContext.BattleStatusEffects.RemoveRange(await dbContext.BattleStatusEffects.Where(entry => entry.RoomId == roomId).ToListAsync());
        dbContext.BattleMonsterSkillCooldowns.RemoveRange(await dbContext.BattleMonsterSkillCooldowns.Where(entry => entry.RoomId == roomId).ToListAsync());
    }

    public async Task RemoveMonsterStateAsync(int roomId, int monsterId)
    {
        dbContext.MonsterIntents.RemoveRange(await dbContext.MonsterIntents
            .Where(entry => entry.RoomId == roomId && entry.MonsterId == monsterId).ToListAsync());
        await Statuses.RemoveBoundToAsync(roomId, "Monster", monsterId);
    }

    public Task<decimal> GetModifierAsync(Room room, string targetType, int targetId, string effectType) =>
        Statuses.ModifierAsync(room, targetType, targetId, effectType);

    private async Task<MonsterSkillDefinition?> SelectSkillAsync(Room room, Monster monster)
    {
        var profile = CatalogFor(room).ResolveProfile(monster.CombatProfileCode);
        if (profile is null || profile.Skills.Length == 0) return null;
        var cooldowns = await dbContext.BattleMonsterSkillCooldowns.Where(entry =>
            entry.RoomId == room.Id && entry.MonsterId == monster.Id).ToListAsync();
        foreach (var local in dbContext.BattleMonsterSkillCooldowns.Local.Where(entry =>
                     entry.RoomId == room.Id && entry.MonsterId == monster.Id))
            if (!cooldowns.Contains(local)) cooldowns.Add(local);
        var eligible = profile.Skills.Select(entry => (Entry: entry, Skill: CatalogFor(room).ResolveSkill(entry.Code)))
            .Where(candidate => candidate.Skill is not null && candidate.Skill.InitialCooldownRounds <= room.RoundNumber &&
                (candidate.Skill.SelfHpBelowPercent is null ||
                 (long)monster.Hp * 100 <= (long)monster.MaxHp * candidate.Skill.SelfHpBelowPercent) &&
                (candidate.Skill.RoomRoundAtLeast is null || room.RoundNumber >= candidate.Skill.RoomRoundAtLeast) &&
                cooldowns.All(cooldown => !string.Equals(cooldown.SkillCode, candidate.Skill.Code, StringComparison.OrdinalIgnoreCase) ||
                    cooldown.ReadyAtRound <= room.RoundNumber)).ToList();
        if (eligible.Count == 0) return null;
        var forced = eligible.Where(candidate => candidate.Skill!.ForcedPriority > 0)
            .OrderByDescending(candidate => candidate.Skill!.ForcedPriority)
            .ThenByDescending(candidate => candidate.Skill!.RoomRoundAtLeast)
            .FirstOrDefault();
        if (forced.Skill is not null) return forced.Skill;
        if (profile.SkillUseChancePercent <= 0 ||
            (random ?? Random.Shared).Next(1, 101) > profile.SkillUseChancePercent) return null;
        var roll = (random ?? Random.Shared).Next(eligible.Sum(candidate => candidate.Entry.Weight));
        foreach (var candidate in eligible)
        {
            if (roll < candidate.Entry.Weight) return candidate.Skill;
            roll -= candidate.Entry.Weight;
        }
        return eligible[^1].Skill;
    }

    private Task<List<BattleStatusEffect>> GetActiveEffectsAsync(Room room, string targetType,
        IReadOnlyCollection<int> targetIds) => Statuses.GetActiveAsync(room, targetType, targetIds);

    private async Task<int?> FindFrontCharacterIdAsync(int roomId) => await (
        from slot in dbContext.RoomSlots
        join character in dbContext.Characters on slot.CharacterId equals character.Id
        where slot.RoomId == roomId && character.Hp > 0
        orderby slot.SlotIndex
        select (int?)character.Id).FirstOrDefaultAsync();

    private async Task<string> GetCharacterTargetLabelAsync(int roomId, int? characterId)
    {
        if (!characterId.HasValue) return "无有效目标";
        var target = await (from slot in dbContext.RoomSlots
            join character in dbContext.Characters on slot.CharacterId equals character.Id
            where slot.RoomId == roomId && character.Id == characterId.Value
            select new { slot.SlotIndex, character.Name }).FirstOrDefaultAsync();
        return target is null ? "目标已失效" : $"{target.SlotIndex} 号位 {target.Name}";
    }
}
