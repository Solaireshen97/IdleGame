using System.Collections.Immutable;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class SkillBattleSnapshotFactory(GameDbContext db, SkillCatalog skills, MonsterCombatService? monsters,
    ProfessionMechanicCatalog? mechanics = null)
{
    public async Task<SkillBattleSnapshot> CaptureAsync(Room room, Monster monster, CharacterSkillDefinition skill,
        int casterId, IEnumerable<(RoomSlot Slot, Character Character)> participants,
        IReadOnlyDictionary<string, int>? professionLevels = null, IReadOnlyList<BattleSkillCooldown>? cooldowns = null,
        bool? canInterruptCurrentIntent = null)
        => ForSkill(await CaptureAutoAsync(room, monster, casterId, participants, professionLevels, cooldowns,
            canInterruptCurrentIntent), skill, mechanics);

    public async Task<SkillBattleSnapshot> CaptureAutoAsync(Room room, Monster monster,
        int casterId, IEnumerable<(RoomSlot Slot, Character Character)> participants,
        IReadOnlyDictionary<string, int>? professionLevels = null, IReadOnlyList<BattleSkillCooldown>? cooldowns = null,
        bool? canInterruptCurrentIntent = null)
    {
        var party = participants.OrderBy(entry => entry.Slot.SlotIndex).ToArray();
        var ids = party.Select(entry => entry.Character.Id).ToArray();
        var effects = monsters is null ? [] : await monsters.Statuses.GetActiveAsync(room, "Character", ids);
        var targets = party.Select(entry => new SkillBattleActor(entry.Character.Id, entry.Slot.SlotIndex,
            entry.Character.Hp, TalentRules.EffectiveMaxHp(entry.Character), effects.Any(effect =>
                effect.TargetId == entry.Character.Id && monsters!.Statuses.IsRemovable(room, effect, false)))).ToImmutableArray();
        var caster = party.Single(entry => entry.Character.Id == casterId).Character;
        professionLevels ??= (await db.CharacterCombatProfessions.Where(entry => entry.CharacterId == casterId).ToListAsync())
            .ToDictionary(entry => entry.ProfessionCode, entry => entry.Level, StringComparer.OrdinalIgnoreCase);
        if (cooldowns is null)
        {
            var entries = await db.BattleSkillCooldowns.Where(entry => entry.RoomId == room.Id && entry.CharacterId == casterId).ToListAsync();
            entries.RemoveAll(entry => db.Entry(entry).State == EntityState.Deleted);
            foreach (var local in db.BattleSkillCooldowns.Local.Where(entry => entry.RoomId == room.Id && entry.CharacterId == casterId))
                if (!entries.Contains(local)) entries.Add(local);
            cooldowns = entries;
        }
        var damageSkills = cooldowns.Where(entry => entry.CharacterId == casterId && entry.ReadyAtRound > room.RoundNumber &&
            !entry.SkillCode.StartsWith(SoulImprintRules.CooldownPrefix) && skills.Resolve(caster, entry.SkillCode, professionLevels) is { } cooling &&
            cooling.Effects.Any(effect => effect.Kind == BattleEffectKind.Damage)).Select(entry => entry.SkillCode).ToImmutableArray();
        var snapshot = new SkillBattleSnapshot(casterId, targets, monster.Hp, monster.MaxHp, monsters is not null,
            monsters is not null && await monsters.Statuses.HasRemovableAsync(room, "Monster", [monster.Id], true),
            monsters is not null && monster.Hp > 0 && room.Status != RoomStatus.BattleOver && room.ClosedAtUtc is null &&
                (canInterruptCurrentIntent ?? await monsters.CanInterruptCurrentIntentAsync(room, monster)),
            monsters?.HasAnyInterruptibleSkill(monster, room) == true, damageSkills);
        return snapshot;
    }

    public static SkillBattleSnapshot ForSkill(SkillBattleSnapshot snapshot, CharacterSkillDefinition skill,
        ProfessionMechanicCatalog? mechanics = null) => snapshot with
    {
        AdditionalSelfCleanse = AcolyteMechanics.NeedsSelfCleanse(skill, mechanics)
    };
}
