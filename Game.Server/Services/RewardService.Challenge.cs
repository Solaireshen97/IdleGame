using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed partial class RewardService
{
    // Receipts and rewards are committed atomically with victory; old frozen definitions have no new rewards.
    public async Task RecordChallengeFirstClearsAsync(Room room, IEnumerable<RewardParticipant> participants,
        DateTime now, List<string> logs)
    {
        var definition = await _depthProgress.DefinitionAsync(room);
        if (definition is null || definition.ChallengeFirstClearQuantities.Count == 0 ||
            room.DepthLevel < definition.ChallengeStartDepth || (await GetRunAsync(room)).Status != "Pending") return;
        var hasClear = dbContext.RewardEvents.Local.Any(item => item.RoomId == room.Id &&
            item.Sequence == room.RunSequence && item.EventKey == "clear") ||
            await dbContext.RewardEvents.AnyAsync(item => item.RoomId == room.Id &&
                item.Sequence == room.RunSequence && item.EventKey == "clear");
        if (!hasClear) return;
        var dungeon = await dbContext.Dungeons.FindAsync(room.DungeonId)
            ?? throw new InvalidOperationException($"Dungeon {room.DungeonId} was not found while recording challenge rewards.");
        var addedEvents = new HashSet<string>();
        foreach (var participant in participants.DistinctBy(item => item.Character.Id))
        {
            if (participant.UserId != participant.Character.UserId) continue;
            if (await dbContext.DungeonRunParticipants.FindAsync(room.Id, room.RunSequence, participant.Character.Id) is null) continue;
            foreach (var reward in await _depthProgress.UnclaimedChallengeFirstClearsAsync(
                dungeon.Code, participant.Character.Id, room.DepthLevel, definition))
            {
                var eventKey = $"challenge-first-clear:{reward.Key}";
                addedEvents.Add(eventKey);
                dbContext.CharacterBattleMilestones.Add(new CharacterBattleMilestone
                {
                    CharacterId = participant.Character.Id, Kind = DungeonDepthProgressService.ChallengeFirstClearKind,
                    TargetCode = DungeonDepthProgressService.ChallengeFirstClearTarget(dungeon.Code, reward.Key),
                    Count = 1, FirstAtUtc = now, LastAtUtc = now
                });
                dbContext.RewardEntries.Add(new RewardEntry
                {
                    RoomId = room.Id, Sequence = room.RunSequence, EventKey = eventKey,
                    UserId = participant.UserId, CharacterId = participant.Character.Id,
                    Kind = "Material", Code = definition.ChallengeFirstClearItemCode,
                    Quantity = reward.Value, RewardSource = "ChallengeFirstClear"
                });
                logs.Add($"{participant.Character.Name} 领取深层LV{reward.Key}首次挑战奖励：通用突破石 × {reward.Value}。");
            }
        }
        foreach (var eventKey in addedEvents)
            if (!dbContext.RewardEvents.Local.Any(item => item.RoomId == room.Id && item.Sequence == room.RunSequence && item.EventKey == eventKey) &&
                !await dbContext.RewardEvents.AnyAsync(item => item.RoomId == room.Id && item.Sequence == room.RunSequence && item.EventKey == eventKey))
                dbContext.RewardEvents.Add(new RewardEvent { RoomId = room.Id, Sequence = room.RunSequence, EventKey = eventKey });
    }
}
