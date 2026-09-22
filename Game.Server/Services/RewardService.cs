using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Game.Server.Services;

public sealed class RewardService(GameDbContext dbContext, RewardCatalog catalog, ProgressionService progression)
{
    public IReadOnlyList<RewardDropPreview> GetDropPreview(string rewardCode, bool isClear) =>
        catalog.GetDropPreview(rewardCode, isClear);

    public bool HasRewardProfile(string rewardCode, bool isClear) =>
        catalog.HasRewardProfile(rewardCode, isClear);

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
        var userIds = entries.Select(entry => entry.UserId).Distinct().ToList();
        var characters = await dbContext.Characters.Where(character => characterIds.Contains(character.Id))
            .ToDictionaryAsync(character => character.Id);
        var users = await dbContext.Users.Where(user => userIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id);
        var stacks = await dbContext.CharacterItemStacks.Where(stack => characterIds.Contains(stack.CharacterId)).ToListAsync();

        var dungeon = victory ? await dbContext.Dungeons.FindAsync(room.DungeonId) : null;
        var tutorial = dungeon?.DungeonKind == "Hunt" ? catalog.FirstHuntWeapon(dungeon.Code) : null;
        if (tutorial is not null)
        {
            const string eventKey = "starter-hunt-weapon";
            var recipients = entries.GroupBy(entry => entry.UserId)
                .Where(group => !users[group.Key].StarterWeaponRewardClaimed)
                .Select(group => (UserId: group.Key, CharacterId: group.Min(entry => entry.CharacterId))).ToList();
            if (recipients.Count > 0)
                dbContext.RewardEvents.Add(new RewardEvent { RoomId = room.Id, Sequence = room.RunSequence, EventKey = eventKey });
            foreach (var recipient in recipients)
            {
                var user = users[recipient.UserId];
                user.StarterWeaponRewardClaimed = true;
                user.Version++;
                var entry = new RewardEntry { RoomId = room.Id, Sequence = room.RunSequence, EventKey = eventKey,
                    UserId = recipient.UserId, CharacterId = recipient.CharacterId, Kind = "Weapon", Code = tutorial.Code,
                    Quantity = 1, WeaponSnapshotJson = JsonSerializer.Serialize(tutorial) };
                entries.Add(entry);
                dbContext.RewardEntries.Add(entry);
                logs.Add($"{characters[recipient.CharacterId].Name} 完成首次普通讨伐，获得新手武器奖励。");
            }
        }

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
                if (gain.ExperienceGained > 0) logs.Add($"{character.Name} 获得 {gain.ExperienceGained} 点经验值。");
                if (gain.LevelsGained > 0) logs.Add($"{character.Name} 升至 Lv.{character.Level}，获得 {gain.LevelsGained} 点天赋点。");
            }
            var gold = group.Where(entry => entry.Kind == "Gold").Sum(entry => entry.Quantity);
            if (gold > 0) logs.Add($"{character.Name} 获得 {gold} 金币。");
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
