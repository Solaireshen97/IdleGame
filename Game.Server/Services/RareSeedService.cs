using System.Security.Cryptography;
using System.Text;
using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class RareSeedService(GameDbContext db, PlantingCatalog plants, DungeonRunRulesService? runRules = null)
{
    public const string FirstSeedClearKind = "RareSeedFirstClear";
    public async Task RecordDropsAsync(Room room, string dungeonCode,
        IEnumerable<RewardParticipant> participants, IEnumerable<int> actualCharacterIds)
    {
        var actual = actualCharacterIds.ToHashSet();
        var recipients = participants.Where(p => actual.Contains(p.Character.Id) && p.UserId == p.Character.UserId)
            .DistinctBy(p => p.Character.Id).ToList();
        if (recipients.Count == 0) return;
        var definition = runRules is null ? null : await runRules.EnsureAsync(room);
        // Seed sources follow the current economy policy, including rooms with older loot snapshots.
        var drops = plants.SeedDropsFor(definition?.DungeonCode ?? dungeonCode, definition?.DepthLevel ?? room.DepthLevel);
        foreach (var plant in drops)
        {
            var eventKey = $"rare-seed:{plant.SeedCode}";
            if (db.RewardEvents.Local.Any(e => e.RoomId == room.Id && e.Sequence == room.RunSequence && e.EventKey == eventKey) ||
                await db.RewardEvents.AnyAsync(e => e.RoomId == room.Id && e.Sequence == room.RunSequence && e.EventKey == eventKey)) continue;
            db.RewardEvents.Add(new RewardEvent { RoomId = room.Id, Sequence = room.RunSequence, EventKey = eventKey });
            foreach (var recipient in recipients)
            {
                var first = plant.FirstClearGuaranteed &&
                    !db.CharacterBattleMilestones.Local.Any(m => m.CharacterId == recipient.Character.Id &&
                        m.Kind == FirstSeedClearKind && m.TargetCode == plant.SeedCode) &&
                    !await db.CharacterBattleMilestones.AnyAsync(m => m.CharacterId == recipient.Character.Id &&
                        m.Kind == FirstSeedClearKind && m.TargetCode == plant.SeedCode);
                // Keep the same roll across transaction retries, including unsuccessful drops.
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{room.Id}:{room.RunSequence}:{recipient.Character.Id}:{plant.SeedCode}"));
                var roll = BitConverter.ToUInt32(hash, 0) % 10000 / 100m;
                if (!first && roll >= plant.DropChancePercent) continue;
                if (first) db.CharacterBattleMilestones.Add(new CharacterBattleMilestone
                {
                    CharacterId = recipient.Character.Id, Kind = FirstSeedClearKind, TargetCode = plant.SeedCode,
                    Count = 1, FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow
                });
                db.RewardEntries.Add(new RewardEntry
                {
                    RoomId = room.Id, Sequence = room.RunSequence, EventKey = eventKey,
                    UserId = recipient.UserId, CharacterId = recipient.Character.Id,
                    Kind = "Material", Code = plant.SeedCode, Quantity = 1,
                    RewardSource = first ? "SeedFirstClear" : "Base"
                });
            }
        }
    }
}
