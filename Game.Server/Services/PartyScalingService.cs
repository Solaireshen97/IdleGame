using Game.Server.Data;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class PartyScalingService(GameDbContext dbContext, PartyScalingCatalog catalog, DungeonRunRulesService? runRules = null)
{
    public PartyScalingCatalog Catalog => catalog;

    public async Task SynchronizeAsync(Room room, IReadOnlyCollection<RoomSlot>? slots = null)
    {
        // Admission after victory belongs to the next run and must not revive the defeated monster.
        if (room.ClosedAtUtc.HasValue || room.Status == RoomStatus.BattleOver) return;
        var percentages = await PercentagesAsync(room);
        if (percentages.All(percent => percent == 100)) return;

        // Read all slots before filtering so pending assignments/removals in this context are included.
        slots ??= await dbContext.RoomSlots.Where(slot => slot.RoomId == room.Id).ToListAsync();
        var partySize = Math.Clamp(slots.Count(slot => slot.CharacterId.HasValue), 1, room.SlotCount);
        var targetSize = room.RoundNumber == 0 ? partySize : Math.Max(room.ScalingPartySize, partySize);
        var percent = percentages[Math.Clamp(targetSize, 1, 5) - 1];
        var monsters = await dbContext.Monsters.Where(monster => monster.RoomId == room.Id).ToListAsync();
        if (monsters.Count == 0 && await dbContext.Monsters.FindAsync(room.MonsterId) is { } legacyMonster)
            monsters.Add(legacyMonster);
        foreach (var monster in monsters)
        {
            if (monster.BaseMaxHp <= 0) monster.BaseMaxHp = monster.MaxHp;
            if (room.RoundNumber > 0 && monster.Hp <= 0) continue;
            var maxHp = checked((int)(((long)monster.BaseMaxHp * percent + 99) / 100));
            if (maxHp == monster.MaxHp) continue;
            // Keep remaining health percentage, rounding up so a living monster never dies on admission.
            monster.Hp = monster.Hp <= 0 ? 0 : checked((int)Math.Min(maxHp,
                ((long)monster.Hp * maxHp + monster.MaxHp - 1) / monster.MaxHp));
            monster.MaxHp = maxHp;
        }
        room.ScalingPartySize = targetSize;
    }

    public async Task<IReadOnlyList<int>> PercentagesAsync(Room room)
    {
        if (runRules is not null) return (await runRules.EnsureAsync(room)).PartyHpPercentages;
        var dungeon = await dbContext.Dungeons.FindAsync(room.DungeonId);
        return dungeon is null ? [100, 100, 100, 100, 100] : catalog.GetHpPercentages(dungeon.PartyScalingProfileCode);
    }
}
