using System.Text.Json;
using Game.Server.Data;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class BattleStatisticsQuery(GameDbContext db, UserService users)
{
    public async Task<(BattleStatisticsResponse? Response, string? Error)> ReadAsync(int roomId, string? token,
        string scope = "current", int? runSequence = null, int? monsterId = null, int? characterId = null)
    {
        if (scope is not ("current" or "run" or "room") || scope == "room" && (runSequence.HasValue || monsterId.HasValue) ||
            scope == "run" && !runSequence.HasValue || runSequence is <= 0 || monsterId is <= 0 || characterId is <= 0)
            return (null, "InvalidStatisticsScope");
        // All rows belong to one SQLite read snapshot; this GET never progresses combat.
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync() : null;
        var room = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == roomId);
        if (room is null) return (null, "NotFound");
        var (user, userError) = await users.GetCurrentUserEntityAsync(token);
        int? userId = userError is null ? user!.Id : null;
        var slots = await db.RoomSlots.AsNoTracking().Where(x => x.RoomId == roomId).ToListAsync();
        if ((room.ClosedAtUtc.HasValue || !room.IsPublic) &&
            (!userId.HasValue || room.OwnerUserId != userId && !slots.Any(x => x.UserId == userId)))
            return (null, "NotFound");
        var selectedRun = scope == "room" ? (int?)null : scope == "current" ? room.RunSequence : runSequence;
        if (selectedRun > room.RunSequence || scope == "current" && runSequence.HasValue && runSequence != room.RunSequence)
            return (null, "InvalidStatisticsScope");
        var runs = await db.BattleRunStatistics.AsNoTracking().Where(x => x.RoomId == roomId &&
            x.RunSequence <= room.RunSequence && (!selectedRun.HasValue || x.RunSequence == selectedRun)).ToListAsync();
        var encounters = await db.BattleEncounterStatistics.AsNoTracking().Where(x => x.RoomId == roomId &&
            x.RunSequence <= room.RunSequence && (!selectedRun.HasValue || x.RunSequence == selectedRun)).ToListAsync();
        if (monsterId.HasValue && !encounters.Any(x => x.MonsterId == monsterId)) return (null, "InvalidStatisticsScope");
        var selectedEncounters = encounters.Where(x => !monsterId.HasValue || x.MonsterId == monsterId).ToList();
        var actors = await db.BattleActorStatistics.AsNoTracking().Where(x => x.RoomId == roomId &&
            x.RunSequence <= room.RunSequence && (!selectedRun.HasValue || x.RunSequence == selectedRun) &&
            (!monsterId.HasValue || x.MonsterId == monsterId)).ToListAsync();
        var abilities = characterId.HasValue ? await db.BattleAbilityStatistics.AsNoTracking().Where(x => x.RoomId == roomId &&
            x.RunSequence <= room.RunSequence && (!selectedRun.HasValue || x.RunSequence == selectedRun) &&
            (!monsterId.HasValue || x.MonsterId == monsterId) && x.CharacterId == characterId).ToListAsync() : [];
        var response = new BattleStatisticsResponse
        {
            RoomId = room.Id, RoomVersion = room.Version, CurrentRunSequence = room.RunSequence, Scope = scope,
            RunSequence = selectedRun, MonsterId = monsterId, CharacterId = characterId, IsClosed = room.ClosedAtUtc.HasValue,
            StatisticsRevision = $"{room.Version}:{scope}:{selectedRun}:{monsterId}:{runs.Max(x => (int?)x.LastSettlementVersion) ?? 0}",
            SchemaVersion = runs.Max(x => (int?)x.SchemaVersion) ?? 1,
            RecordedRounds = monsterId.HasValue ? selectedEncounters.Sum(x => x.RecordedRounds) : runs.Sum(x => x.RecordedRounds),
            LastRoundNumber = monsterId.HasValue ? selectedEncounters.Max(x => (int?)x.LastRound) ?? 0 :
                runs.OrderByDescending(x => x.RunSequence).FirstOrDefault()?.LastAggregatedRound ?? 0,
            CoverageStartRound = runs.Min(x => (int?)x.CoverageStartRound), RecordedRuns = runs.Count,
            CompletedRuns = runs.Count(x => x.EndedAtUtc.HasValue), PartialRuns = runs.Count(x => x.CoverageStartRound > 1),
            Outcome = selectedRun.HasValue ? runs.SingleOrDefault()?.Outcome ??
                (selectedRun != room.RunSequence ? "Unknown" : room.Status == RoomStatus.BattleOver || room.ClosedAtUtc.HasValue ? "Stopped" : "InProgress") : "Aggregate",
            UnattributedDamage = selectedEncounters.Sum(x => x.UnattributedDamage),
            UnattributedHealing = selectedEncounters.Sum(x => x.UnattributedHealing),
            Encounters = scope == "room" ? [] : encounters.OrderBy(x => x.RunSequence).ThenBy(x => x.WaveNumber).ThenBy(x => x.Position)
                .Select(x => new BattleStatisticsEncounterResponse { MonsterId = x.MonsterId, Name = x.Name,
                    WaveNumber = x.WaveNumber, Position = x.Position, IsBoss = x.IsBoss, RecordedRounds = x.RecordedRounds }).ToList()
        };
        var currentUnstarted = room.RoundNumber == 0 && room.Status != RoomStatus.BattleOver && !room.ClosedAtUtc.HasValue;
        var currentEmpty = room.RoundNumber == 0 && !runs.Any(x => x.RunSequence == room.RunSequence);
        var expectedRuns = scope == "room" ? room.RunSequence - (currentEmpty ? 1 : 0) : selectedRun == room.RunSequence && currentEmpty ? 0 : 1;
        response.MissingRuns = Math.Max(0, expectedRuns - runs.Count);
        response.Coverage = currentUnstarted && response.RecordedRounds == 0 && (selectedRun == room.RunSequence || expectedRuns == 0)
            ? "NotStarted" : runs.Count == 0 ? "Unrecorded" : response.MissingRuns > 0 || response.PartialRuns > 0 ||
                runs.Select(x => x.SchemaVersion).Distinct().Count() > 1 ? "Partial" : "Complete";
        if (room.RoundNumber == 0 && !currentUnstarted &&
            (selectedRun == room.RunSequence || scope == "room" && expectedRuns == 0))
        {
            response.Coverage = "Complete";
            response.Outcome = "NoBattle";
        }
        if (actors.Count == 0 && selectedRun == room.RunSequence && currentUnstarted)
        {
            var ids = slots.Where(x => x.CharacterId.HasValue).Select(x => x.CharacterId!.Value).ToArray();
            var characters = await db.Characters.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync();
            actors = characters.Select(x => { var slot = slots.Single(s => s.CharacterId == x.Id); return new BattleActorStatistics
            { CharacterId = x.Id, UserId = slot.UserId, Name = x.Name, ProfessionCode = x.ProfessionCode ?? "",
                SlotIndex = slot.SlotIndex, WasAlive = x.Hp > 0 }; }).ToList();
        }
        response.Totals = Sum(actors);
        if (characterId.HasValue && !actors.Any(x => x.CharacterId == characterId)) return (null, "InvalidStatisticsScope");
        response.Actors = actors.GroupBy(x => x.CharacterId).OrderBy(x => x.Key).Select(group =>
        {
            var rows = group.OrderByDescending(x => x.RunSequence).ThenByDescending(x => x.LastObservedRound).ToList();
            var latest = rows[0];
            var identity = rows.FirstOrDefault(x => !string.IsNullOrEmpty(x.Name)) ?? latest;
            var configurations = rows.SelectMany(x => ParseConfigurations(x.ConfigurationsJson)).Distinct().ToList();
            var own = userId.HasValue && rows.Any(x => x.UserId == userId);
            var metrics = Sum(rows);
            return new BattleStatisticsActorResponse
            {
                CharacterId = group.Key, Name = identity.Name, ProfessionCode = identity.ProfessionCode, Element = identity.Element,
                SlotIndex = latest.SlotIndex, IsOwn = own, IsPresent = (scope == "room" || selectedRun == room.RunSequence) &&
                    slots.Any(x => x.CharacterId == group.Key), WasAlive = latest.WasAlive,
                HasUnknownIdentity = string.IsNullOrEmpty(identity.Name), HasMixedConfigurations = configurations.Count > 1,
                Configurations = own ? configurations : [], Metrics = metrics,
                DamagePerRound = response.RecordedRounds > 0 ? metrics.DamageDealt / (decimal)response.RecordedRounds : null,
                DamageSharePercent = response.Totals.DamageDealt > 0 ? metrics.DamageDealt * 100m / response.Totals.DamageDealt : null,
                Abilities = characterId == group.Key ? abilities.GroupBy(x => (x.ActionKind, x.SourceCode))
                    .Select(g => new BattleStatisticsAbilityResponse { ActionKind = g.Key.ActionKind, SourceCode = g.Key.SourceCode,
                        Label = g.First().Label, DamageDealt = g.Sum(x => x.DamageDealt), HealingDone = g.Sum(x => x.HealingDone),
                        SelfHealing = g.Sum(x => x.SelfHealing), PotionHealing = g.Sum(x => x.PotionHealing) })
                    .OrderByDescending(x => x.DamageDealt).ThenBy(x => x.SourceCode).ToList() : []
            };
        }).Where(x => !characterId.HasValue || x.CharacterId == characterId).ToList();
        return (response, null);
    }

    private static IEnumerable<BattleStatisticsConfiguration> ParseConfigurations(string value)
    {
        try { return JsonSerializer.Deserialize<List<BattleStatisticsConfiguration>>(value) ?? []; }
        catch (JsonException) { return []; }
    }

    private static BattleStatisticsMetrics Sum(IEnumerable<BattleActorStatistics> source)
    {
        var rows = source.ToList();
        return new() { DamageDealt = rows.Sum(x => x.DamageDealt), DamageTaken = rows.Sum(x => x.DamageTaken),
            HealingDone = rows.Sum(x => x.HealingDone), HealingReceived = rows.Sum(x => x.HealingReceived),
            SelfHealing = rows.Sum(x => x.SelfHealing), PotionHealing = rows.Sum(x => x.PotionHealing), Deaths = rows.Sum(x => x.Deaths),
            Cleanses = rows.Sum(x => x.Cleanses), Dispels = rows.Sum(x => x.Dispels), Interrupts = rows.Sum(x => x.Interrupts),
            PresentRounds = rows.Sum(x => x.PresentRounds), AliveRounds = rows.Sum(x => x.AliveRounds) };
    }
}
