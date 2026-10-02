using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared.Dtos.Story;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class StoryService(GameDbContext db, UserService users, StoryQuestCatalog catalog,
    ProductionService? production = null, MaterialCatalog? materials = null, DungeonDepthCatalog? depths = null)
{
    public static async Task StartTrackedAsync(GameDbContext db, int userId, int characterId, StoryQuestCatalog catalog, DateTime now)
    {
        var state = await db.UserStoryStates.FindAsync(userId);
        if (state is null) return; // Imported accounts are initialized explicitly, never in a read.
        if (state.TutorialCharacterId is null) { state.TutorialCharacterId = characterId; state.Version++; }
        if (state.CurrentQuestCode is null && !state.ChapterCompleted)
            await ActivateAsync(db, state, catalog.FirstQuest, now);
        else if (state.CurrentQuestCode is { } current && await db.StoryQuestProgress.FindAsync(userId, current) is { ActorCharacterId: null } quest)
        {
            if (StoryProgressService.Definition(quest).ActorPolicy == "TutorialCharacter")
            { quest.ActorCharacterId = state.TutorialCharacterId; quest.Version++; state.Version++; }
        }
    }

    private static async Task ActivateAsync(GameDbContext db, UserStoryState state, StoryQuestDefinition definition, DateTime now)
    {
        if (await db.StoryQuestProgress.FindAsync(state.UserId, definition.Code) is not null)
            throw new InvalidOperationException("A completed story quest cannot be reactivated.");
        var quest = new StoryQuestProgress { UserId = state.UserId, QuestCode = definition.Code,
            DefinitionJson = JsonSerializer.Serialize(definition), ActivatedAtUtc = now,
            ActorCharacterId = definition.ActorPolicy == "TutorialCharacter" ? state.TutorialCharacterId : null };
        db.StoryQuestProgress.Add(quest);
        state.CurrentQuestCode = definition.Code; state.ChapterCode = definition.ChapterCode; state.Version++;
        if (state.TutorialCharacterId is { } id) await StoryProgressService.RefreshAsync(db, id, now);
    }

    public async Task<(StoryOverviewResponse? Response, string? Error)> GetAsync(string? token)
    {
        var (user, error) = await users.GetCurrentUserEntityAsync(token);
        return error is not null ? (null, error) : (await OverviewAsync(user!), null);
    }

    public async Task<(StoryOverviewResponse? Response, string? Error)> InitializeAsync(string? token)
    {
        var (user, character, error) = await users.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var state = await db.UserStoryStates.FindAsync(user!.Id);
            if (state is null)
            {
                state = new UserStoryState { UserId = user.Id, IsLegacy = true, TutorialCharacterId = character!.Id };
                db.UserStoryStates.Add(state);
            }
            if (production is not null) await production.SettleCharacterTrackedAsync(character!.Id, DateTime.UtcNow);
            await StartTrackedAsync(db, user.Id, character!.Id, catalog, DateTime.UtcNow);
            if (state.TutorialCharacterId is { } actor) await StoryProgressService.RefreshAsync(db, actor, DateTime.UtcNow);
            await db.SaveChangesAsync(); await transaction.CommitAsync();
            return (await OverviewAsync(user), null);
        }
        catch (Exception ex) when (DatabaseWriteErrors.IsConflict(ex))
        {
            await transaction.RollbackAsync(); db.ChangeTracker.Clear();
            return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(StoryOverviewResponse? Response, string? Error)> TurnInAsync(string? token, string questCode, StoryTurnInRequest request)
    {
        var (user, error) = await users.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        if (!RequestId(request.RequestId, out var requestId) || request.ExpectedVersion < 0) return (null, "InvalidRequest");
        var fingerprint = "turn-in:" + questCode;
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            if (await db.StoryActionReceipts.FindAsync(user!.Id, requestId) is { } receipt)
                return receipt.Fingerprint == fingerprint ? (await OverviewAsync(user), null) : (null, "RequestIdConflict");
            var (state, quest) = await StoryProgressService.CurrentAsync(db, user.Id);
            var alreadyClaimed = await db.StoryActionReceipts.SingleOrDefaultAsync(x => x.UserId == user.Id && x.QuestCode == questCode);
            if (alreadyClaimed is not null)
            {
                // Remember successful replay keys too: they cannot later be reused for another quest.
                db.StoryActionReceipts.Add(new StoryActionReceipt { UserId = user.Id, RequestId = requestId,
                    Fingerprint = fingerprint, RecipientCharacterId = alreadyClaimed.RecipientCharacterId,
                    RewardsJson = "{}", CompletedAtUtc = DateTime.UtcNow });
                await db.SaveChangesAsync(); await transaction.CommitAsync();
                return (await OverviewAsync(user), null);
            }
            if (state is null || quest is null || quest.QuestCode != questCode) return (null, "StoryQuestUnavailable");
            if (quest.Version != request.ExpectedVersion) return (null, "ConcurrencyConflict");
            if (quest.Status != "ReadyToTurnIn") return (null, "StoryObjectiveIncomplete");
            var actorId = quest.ActorCharacterId ?? state.TutorialCharacterId;
            var actor = actorId.HasValue ? await db.Characters.FindAsync(actorId.Value) : null;
            if (actor is null || actor.UserId != user.Id) return (null, "StoryCharacterRequired");
            var definition = StoryProgressService.Definition(quest);
            var now = DateTime.UtcNow;
            // All older production is settled before any new quest materials or activation timestamps.
            if (production is not null)
            {
                await production.SettleCharacterTrackedAsync(actor.Id, now);
                if (state.TutorialCharacterId is { } tutorialId && tutorialId != actor.Id)
                    await production.SettleCharacterTrackedAsync(tutorialId, now);
            }
            if (!state.IsLegacy)
            {
                actor.Gold = checked(actor.Gold + definition.RewardGold);
                foreach (var reward in definition.Rewards)
                {
                    var stack = db.CharacterItemStacks.Local.FirstOrDefault(x => x.CharacterId == actor.Id && x.ItemCode == reward.Code)
                        ?? await db.CharacterItemStacks.SingleOrDefaultAsync(x => x.CharacterId == actor.Id && x.ItemCode == reward.Code);
                    if (stack is null)
                    {
                        stack = new CharacterItemStack { CharacterId = actor.Id, ItemCode = reward.Code };
                        db.CharacterItemStacks.Add(stack);
                    }
                    stack.Quantity = checked(stack.Quantity + reward.Quantity); stack.Version++;
                }
            }
            // Guard against a concurrent recipient deletion, including a zero-reward dialogue.
            actor.Version++;
            quest.Status = "TurnedIn"; quest.TurnedInAtUtc = now; quest.Version++; state.Version++;
            var flags = JsonSerializer.Deserialize<HashSet<string>>(state.StoryFlagsJson) ?? [];
            flags.Add(quest.QuestCode); state.StoryFlagsJson = JsonSerializer.Serialize(flags);
            foreach (var node in definition.UnlockMapNodeCodes)
                if (!db.StoryMapUnlocks.Local.Any(x => x.UserId == user.Id && x.MapNodeCode == node) &&
                    !await db.StoryMapUnlocks.AnyAsync(x => x.UserId == user.Id && x.MapNodeCode == node))
                    db.StoryMapUnlocks.Add(new StoryMapUnlock { UserId = user.Id, MapNodeCode = node,
                        SourceQuestCode = questCode, UnlockedAtUtc = now });
            if (definition.NextQuestCode is { Length: > 0 } next)
                await ActivateAsync(db, state, catalog.FindQuest(next) ?? throw new InvalidOperationException("Missing next quest."), now);
            else { state.CurrentQuestCode = null; state.ChapterCompleted = true; }
            db.StoryActionReceipts.Add(new StoryActionReceipt { UserId = user.Id, RequestId = requestId,
                Fingerprint = fingerprint, QuestCode = questCode, RecipientCharacterId = actor.Id, CompletedAtUtc = now,
                RewardsJson = JsonSerializer.Serialize(new { Gold = state.IsLegacy ? 0 : definition.RewardGold,
                    Items = state.IsLegacy ? [] : definition.Rewards, definition.UnlockMapNodeCodes }) });
            await db.SaveChangesAsync(); await transaction.CommitAsync();
            return (await OverviewAsync(user), null);
        }
        catch (Exception ex) when (DatabaseWriteErrors.IsConflict(ex) || ex is OverflowException)
        {
            await transaction.RollbackAsync(); db.ChangeTracker.Clear();
            return (null, ex is OverflowException ? "InventoryFull" : "ConcurrencyConflict");
        }
    }

    public async Task<(StoryOverviewResponse? Response, string? Error)> ChangeCharacterAsync(string? token, StoryTutorialCharacterRequest request)
    {
        var (user, error) = await users.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        if (!RequestId(request.RequestId, out var requestId) || request.ExpectedVersion < 0) return (null, "InvalidRequest");
        var fingerprint = "actor:" + request.CharacterId;
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            if (await db.StoryActionReceipts.FindAsync(user!.Id, requestId) is { } receipt)
                return receipt.Fingerprint == fingerprint ? (await OverviewAsync(user), null) : (null, "RequestIdConflict");
            var actor = await db.Characters.FindAsync(request.CharacterId);
            if (actor is null || actor.UserId != user.Id) return (null, "NotOwner");
            var (state, quest) = await StoryProgressService.CurrentAsync(db, user.Id);
            if (state is null) return (null, "StoryQuestUnavailable");
            if (state.Version != request.ExpectedVersion) return (null, "ConcurrencyConflict");
            var now = DateTime.UtcNow;
            if (production is not null) await production.SettleCharacterTrackedAsync(actor.Id, now);
            var changed = state.TutorialCharacterId != actor.Id;
            state.TutorialCharacterId = actor.Id; state.Version++; actor.Version++;
            if (quest is not null && (StoryProgressService.Definition(quest).ActorPolicy == "TutorialCharacter" ||
                    quest.ActorCharacterId is null || !await db.Characters.AnyAsync(x => x.Id == quest.ActorCharacterId && x.UserId == user.Id)))
            {
                if (changed || quest.ActorCharacterId != actor.Id)
                {
                    quest.ActorCharacterId = actor.Id; quest.Progress = 0; quest.Status = "Active";
                    quest.CompletedAtUtc = null; quest.ActivatedAtUtc = now; quest.Version++;
                }
                await StoryProgressService.RefreshAsync(db, actor.Id, now);
            }
            db.StoryActionReceipts.Add(new StoryActionReceipt { UserId = user.Id, RequestId = requestId,
                Fingerprint = fingerprint, RecipientCharacterId = actor.Id, RewardsJson = "{}", CompletedAtUtc = now });
            await db.SaveChangesAsync(); await transaction.CommitAsync();
            return (await OverviewAsync(user), null);
        }
        catch (Exception ex) when (DatabaseWriteErrors.IsConflict(ex))
        {
            await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return (null, "ConcurrencyConflict");
        }
    }

    private static bool RequestId(string? raw, out string id)
    {
        id = "";
        if (!Guid.TryParse(raw, out var guid) || guid == Guid.Empty) return false;
        id = guid.ToString("N"); return true;
    }

    private async Task<StoryOverviewResponse> OverviewAsync(User user)
    {
        var state = await db.UserStoryStates.FindAsync(user.Id);
        var actor = state?.TutorialCharacterId is { } id ? await db.Characters.FindAsync(id) : null;
        var instances = await db.StoryQuestProgress.Where(x => x.UserId == user.Id).ToDictionaryAsync(x => x.QuestCode);
        var response = new StoryOverviewResponse { UserId = user.Id, Version = state?.Version ?? 0,
            TutorialCharacterId = actor?.Id, TutorialCharacterName = actor?.Name,
            NeedsTutorialCharacter = actor is null, IsLegacy = state?.IsLegacy ?? true,
            ChapterCode = state?.ChapterCode ?? catalog.FirstQuest.ChapterCode,
            ChapterCompleted = state?.ChapterCompleted ?? false };
        response.ChapterName = catalog.Chapters.FirstOrDefault(x => x.Code == response.ChapterCode)?.Name ?? response.ChapterCode;
        var visibleDefinitions = catalog.Quests.Concat(instances.Values.Select(StoryProgressService.Definition))
            .DistinctBy(x => x.Code).OrderBy(x => x.Order);
        foreach (var current in visibleDefinitions)
        {
            var instance = instances.GetValueOrDefault(current.Code);
            var definition = instance is null ? current : StoryProgressService.Definition(instance);
            var item = new StoryQuestResponse { Code = definition.Code, ChapterCode = definition.ChapterCode,
                Name = definition.Name, Summary = definition.Summary, Status = instance?.Status ?? "Locked",
                Version = instance?.Version ?? 0, Progress = instance?.Progress ?? 0, RequiredCount = definition.RequiredCount,
                ActorCharacterId = instance?.ActorCharacterId ?? actor?.Id,
                NpcName = catalog.Npcs.FirstOrDefault(x => x.Code == definition.TurnInNpcCode)?.Name ?? definition.TurnInNpcCode,
                StartDialogue = definition.StartDialogue, TurnInDialogue = definition.TurnInDialogue,
                RewardGold = response.IsLegacy ? 0 : definition.RewardGold,
                Rewards = response.IsLegacy ? [] : definition.Rewards.Select(x => new StoryItemRewardResponse
                    { Code = x.Code, Name = materials?.FindItem(x.Code)?.Name ?? x.Code, Quantity = x.Quantity }).ToList(),
                Destination = definition.Destination };
            response.Quests.Add(item);
            if (item.Code == state?.CurrentQuestCode) response.CurrentQuest = item;
        }
        if (response.CurrentQuest?.ActorCharacterId is { } questActor &&
            !await db.Characters.AnyAsync(x => x.Id == questActor && x.UserId == user.Id)) response.NeedsTutorialCharacter = true;
        var access = new CampaignAccessService(db, depths);
        var nodes = catalog.MapNodes;
        var codes = nodes.Select(x => x.DungeonCode).ToArray();
        var dungeons = await db.Dungeons.Where(x => codes.Contains(x.Code)).ToDictionaryAsync(x => x.Code);
        var clears = await db.UserDungeonClears.Where(x => x.UserId == user.Id).Select(x => x.DungeonId).ToListAsync();
        var depthProgress = new DungeonDepthProgressService(db, depths);
        foreach (var node in nodes)
        {
            var dungeon = dungeons.GetValueOrDefault(node.DungeonCode);
            var locked = dungeon is null ? "DungeonNotFound" : await access.AdmissionErrorAsync(user.Id, dungeon);
            var admission = locked ?? (dungeon is null ? "DungeonNotFound" : await depthProgress.AdmissionErrorAsync(user.Id, dungeon, 1));
            response.MapNodes.Add(new StoryMapNodeResponse { Code = node.Code, DungeonCode = node.DungeonCode,
                DungeonId = dungeon?.Id, Name = node.Name, RegionCode = node.RegionCode, Description = node.Description,
                CanEnter = admission is null, IsUnlocked = locked is null, IsCleared = dungeon is not null && clears.Contains(dungeon.Id),
                LockReason = admission == "StoryMapLocked" ? "完成前置剧情后开放" : admission == "DungeonDepthLocked" ? "需先通关本地区普通副本" : admission,
                RequiredQuestCode = node.RequiredQuestCode, RecommendedLevel = dungeon?.RecommendedLevel ?? 1 });
        }
        return response;
    }
}
