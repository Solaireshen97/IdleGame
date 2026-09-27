using System.Security.Cryptography;
using System.Text;
using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class RareSeedService(GameDbContext db, PlantingCatalog plants)
{
    public async Task RecordDropsAsync(Room room, string dungeonCode,
        IEnumerable<RewardParticipant> participants, IEnumerable<int> actualCharacterIds)
    {
        var actual = actualCharacterIds.ToHashSet();
        var recipients = participants.Where(p => actual.Contains(p.Character.Id))
            .DistinctBy(p => p.Character.Id).ToList();
        if (recipients.Count == 0) return;
        foreach (var plant in plants.Plants.Where(p => p.IsRare &&
                     (p.UnlockTargetCode == dungeonCode || p.AlternativeUnlockTargetCodes.Contains(dungeonCode))))
        {
            var eventKey = $"rare-seed:{plant.SeedCode}";
            if (db.RewardEvents.Local.Any(e => e.RoomId == room.Id && e.Sequence == room.RunSequence && e.EventKey == eventKey) ||
                await db.RewardEvents.AnyAsync(e => e.RoomId == room.Id && e.Sequence == room.RunSequence && e.EventKey == eventKey)) continue;
            db.RewardEvents.Add(new RewardEvent { RoomId = room.Id, Sequence = room.RunSequence, EventKey = eventKey });
            foreach (var recipient in recipients)
            {
                // Keep the same roll across transaction retries, including unsuccessful drops.
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{room.Id}:{room.RunSequence}:{recipient.Character.Id}:{plant.SeedCode}"));
                var roll = BitConverter.ToUInt32(hash, 0) % 10000 / 100m;
                if (roll >= plant.DropChancePercent) continue;
                db.RewardEntries.Add(new RewardEntry
                {
                    RoomId = room.Id, Sequence = room.RunSequence, EventKey = eventKey,
                    UserId = recipient.UserId, CharacterId = recipient.Character.Id,
                    Kind = "Material", Code = plant.SeedCode, Quantity = 1
                });
            }
        }
    }
}
