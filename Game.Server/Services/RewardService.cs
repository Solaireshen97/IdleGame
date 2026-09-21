using System.Text.Json;
using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class RewardService(GameDbContext dbContext, RewardCatalog catalog, ProgressionService progression)
{
    public async Task RecordAsync(Room room, string dungeonCode, IEnumerable<RewardParticipant> participants,
        string eventKey, bool isClear)
    {
        var run = await GetRunAsync(room);
        if (run.Status != "Pending" || dbContext.RewardEvents.Local.Any(entry =>
                entry.RoomId == room.Id && entry.Sequence == room.RunSequence && entry.EventKey == eventKey) ||
            await dbContext.RewardEvents.AnyAsync(entry => entry.RoomId == room.Id &&
                entry.Sequence == room.RunSequence && entry.EventKey == eventKey)) return;

        dbContext.RewardEvents.Add(new RewardEvent
        {
            RoomId = room.Id, Sequence = room.RunSequence, EventKey = eventKey
        });
        foreach (var participant in participants.DistinctBy(entry => entry.Character.Id))
            dbContext.RewardEntries.AddRange(catalog.Roll(dungeonCode, isClear, room.Id, room.RunSequence,
                eventKey, participant.UserId, participant.Character.Id));
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
        var userIds = entries.Where(entry => entry.Kind == "Gold").Select(entry => entry.UserId).Distinct().ToList();
        var characters = await dbContext.Characters.Where(character => characterIds.Contains(character.Id))
            .ToDictionaryAsync(character => character.Id);
        var users = await dbContext.Users.Where(user => userIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id);
        var stacks = await dbContext.CharacterItemStacks.Where(stack => characterIds.Contains(stack.CharacterId)).ToListAsync();

        foreach (var group in entries.GroupBy(entry => entry.UserId))
        {
            var gold = group.Where(entry => entry.Kind == "Gold").Sum(entry => entry.Quantity);
            if (gold <= 0) continue;
            var user = users[group.Key];
            user.Gold = checked(user.Gold + gold);
            user.Version++;
        }
        foreach (var group in entries.GroupBy(entry => entry.CharacterId))
        {
            var character = characters[group.Key];
            var experience = group.Where(entry => entry.Kind == "Experience").Sum(entry => entry.Quantity);
            if (experience > 0)
            {
                var gain = progression.AwardExperience(character, experience);
                if (gain.ExperienceGained > 0) logs.Add($"{character.Name} gains {gain.ExperienceGained} EXP.");
                if (gain.LevelsGained > 0) logs.Add($"{character.Name} reached Lv.{character.Level} and gained {gain.LevelsGained} talent point(s).");
            }
            var gold = group.Where(entry => entry.Kind == "Gold").Sum(entry => entry.Quantity);
            if (gold > 0) logs.Add($"{character.Name} receives {gold} gold.");
            foreach (var items in group.Where(entry => entry.Kind == "Consumable").GroupBy(entry => entry.Code))
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
                logs.Add($"{character.Name} receives {quantity} {catalog.Describe(items.First())}.");
            }
            foreach (var weapon in group.Where(entry => entry.Kind == "Weapon"))
            {
                var snapshot = JsonSerializer.Deserialize<WeaponRewardSnapshot>(weapon.WeaponSnapshotJson!)
                    ?? throw new InvalidOperationException("Missing weapon reward snapshot.");
                for (var i = 0; i < weapon.Quantity; i++)
                    dbContext.CharacterWeapons.Add(snapshot.ToCharacterWeapon(character.Id));
                logs.Add($"{character.Name} receives {weapon.Quantity} {snapshot.Name}.");
            }
        }
        run.Status = victory ? "Victory" : "Defeat";
        run.SettledAtUtc = now;
        logs.Add(victory ? "Dungeon rewards settled." : "Earned kill rewards settled after defeat.");
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
