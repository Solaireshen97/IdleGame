using Game.Server.Data;
using Game.Server.Configuration;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Game.Server.Services;

public partial class RoomService(GameDbContext dbContext, UserService userService, ProgressionService progressionService, ConsumableCatalog consumableCatalog, SkillCatalog skillCatalog, RewardService rewardService, DungeonEncounterCatalog? encounterCatalog = null, MonsterCombatService? monsterCombatService = null, BattleLogStore? battleLogStore = null, WorldCatalog? worldCatalog = null, IOptions<ActivityOptions>? activityOptions = null, MaterialCatalog? materialCatalog = null, WeaponCatalog? weaponCatalog = null, SoulImprintCatalog? soulImprintCatalog = null, PartyScalingService? partyScalingService = null, DungeonDepthCatalog? depthCatalog = null, DungeonDepthProgressService? depthProgress = null)
{
    private readonly DungeonDepthCatalog _depthCatalog = depthCatalog ?? new(Options.Create(new DungeonDepthOptions()));
    private readonly DungeonDepthProgressService _depthProgress = depthProgress ?? new(dbContext, depthCatalog);
    private readonly PartyScalingService _partyScaling = partyScalingService ?? new(dbContext, PartyScalingCatalog.Default);
    private const int SlotCount = 5;
    private TimeSpan MaximumActivityDuration => TimeSpan.FromHours(activityOptions?.Value.MaximumHours is > 0 and <= 168 ? activityOptions.Value.MaximumHours : 12);

    public async Task<List<RoomSummaryResponse>> GetRoomsAsync(string? token = null)
    {
        var (currentUser, userError) = await userService.GetCurrentUserEntityAsync(token);
        var currentUserId = userError is null ? currentUser!.Id : (int?)null;
        var rooms = await dbContext.Rooms.Where(room => room.ClosedAtUtc == null && room.IsPublic ||
            currentUserId.HasValue && (room.OwnerUserId == currentUserId.Value ||
                dbContext.RoomSlots.Any(slot => slot.RoomId == room.Id && slot.UserId == currentUserId.Value))).ToListAsync();
        var result = new List<RoomSummaryResponse>();
        foreach (var room in rooms)
        {
            var summary = await BuildRoomSummaryAsync(room, currentUserId);
            if (summary is not null) result.Add(summary);
        }
        return result;
    }

    public async Task<RoomDetailResponse?> GetRoomDetailAsync(int roomId, string? token = null)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null) return null;
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if ((room.ClosedAtUtc.HasValue || !room.IsPublic) && (error is not null ||
            room.OwnerUserId != user!.Id && !await dbContext.RoomSlots.AnyAsync(slot => slot.RoomId == roomId && slot.UserId == user.Id)))
            return null;
        return await BuildRoomDetailAsync(room, error is null ? user!.Id : null);
    }

    public async Task<List<DungeonSummaryResponse>> GetDungeonsAsync(string? token)
    {
        var (user, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        var clearedDungeonCodes = error is null
            ? await dbContext.CharacterBattleMilestones.Where(milestone =>
                    milestone.CharacterId == character!.Id &&
                    milestone.Kind == BattleMilestoneService.DungeonClearKind && milestone.Count > 0)
                .Select(milestone => milestone.TargetCode).ToListAsync()
            : [];
        var currentLevel = error is null ? character!.Level : 0;
        var dungeons = await dbContext.Dungeons.Where(dungeon => dungeon.IsVisible)
            .OrderBy(dungeon => dungeon.SortOrder).ToListAsync();
        var result = new List<DungeonSummaryResponse>();
        foreach (var dungeon in dungeons)
        {
            var canEnter = error is null;
            var definition = _depthCatalog.Find(dungeon.Code);
            var highest = definition is not null && character is not null ? await _depthProgress.HighestAsync(character.Id, dungeon.Id) : 0;
            var mastery = Math.Min(4, highest);
            var unlocked = definition is not null && user is not null ? await _depthProgress.UnlockedAsync(user.Id, dungeon.Id, definition.MaximumDepth) : 1;
            result.Add(new DungeonSummaryResponse
            {
                SupportsDepths = definition is not null, Stage = definition?.Stage ?? 0,
                MaximumDepth = definition?.MaximumDepth ?? 1, UnlockedDepth = unlocked,
                CharacterHighestDepth = highest, MasteryLevel = mastery, UsesPlaceholderBalance = definition is not null,
                GoldBonusPercent = mastery >= 2 ? definition!.GoldBonusPercent : 0,
                KillExtraRollChancePercent = mastery >= 3 ? definition!.KillExtraRollChancePercent : 0,
                ClearExtraRollChancePercent = mastery >= 4 ? definition!.ClearExtraRollChancePercent : 0,
                Depths = definition is null ? [] : Enumerable.Range(1, definition.MaximumDepth).Select(depth => new DungeonDepthPreviewResponse
                {
                    DepthLevel = depth, IsUnlocked = depth <= unlocked, IsChallenge = depth >= definition.ChallengeStartDepth,
                    StatMultiplier = _depthCatalog.StatMultiplier(dungeon.Code, depth),
                    AddedMechanics = _depthCatalog.AddedMechanics(dungeon.Code, depth).ToList()
                }).ToList(),
                DungeonId = dungeon.Id, Code = dungeon.Code, Name = dungeon.Name,
                RegionCode = dungeon.RegionCode, RegionName = dungeon.RegionName, DungeonKind = dungeon.DungeonKind,
                PartyScalingProfileCode = dungeon.PartyScalingProfileCode,
                PartyHpPercentages = _partyScaling.Catalog.GetHpPercentages(dungeon.PartyScalingProfileCode).ToList(),
                Description = dungeon.Description, MinimumLevel = dungeon.MinimumLevel,
                RecommendedLevel = dungeon.RecommendedLevel, CurrentCharacterLevel = currentLevel,
                ExperiencePercent = progressionService.ApplyDungeonExperienceModifier(100, currentLevel, dungeon.ExperienceReferenceLevel),
                CanEnter = canEnter && unlocked > 0,
                LockReason = !canEnter ? "请先选择角色" : unlocked == 0 ? "账号需先通关本地区普通副本" : null,
                MonsterName = dungeon.MonsterName, MonsterElement = dungeon.MonsterElement,
                MonsterMaxHp = dungeon.MonsterMaxHp, MonsterAttack = dungeon.MonsterAttack,
                MonsterDefense = dungeon.MonsterDefense, SlotCount = dungeon.SlotCount,
                WaveCount = encounterCatalog?.GetWaveCount(dungeon) ?? 1,
                MonsterCount = encounterCatalog?.GetMonsterCount(dungeon) ?? 1,
                IsClearedByCurrentUser = clearedDungeonCodes.Contains(dungeon.Code),
                AutoUnlocked = clearedDungeonCodes.Contains(dungeon.Code),
                Monsters = (encounterCatalog?.CreateMonsters(dungeon) ?? [new Monster
                {
                    Name = dungeon.MonsterName, Element = dungeon.MonsterElement, MaxHp = dungeon.MonsterMaxHp,
                    Attack = dungeon.MonsterAttack, Defense = dungeon.MonsterDefense, WaveNumber = 1, Position = 1,
                    RewardProfileCode = dungeon.Code
                }]).Select(monster => BuildMonsterPreview(dungeon, monster)).ToList(),
                RewardPreview = BuildRewardPreview(dungeon)
            });
        }
        return result;
    }

    public async Task<DungeonSummaryResponse?> GetDungeonAsync(int dungeonId, string? token, int depthLevel = 1)
    {
        var result = (await GetDungeonsAsync(token)).SingleOrDefault(dungeon => dungeon.DungeonId == dungeonId);
        if (result is null || !_depthCatalog.ValidateDepth(result.Code, depthLevel)) return null;
        var dungeon = (await dbContext.Dungeons.FindAsync(dungeonId))!;
        result.DepthLevel = depthLevel;
        if (result.SupportsDepths)
        {
            result.Name = _depthCatalog.DisplayName(dungeon.Name, depthLevel);
            result.MonsterMaxHp = _depthCatalog.ScaleStat(dungeon.MonsterMaxHp, depthLevel, dungeon.Code);
            result.MonsterAttack = _depthCatalog.ScaleStat(dungeon.MonsterAttack, depthLevel, dungeon.Code);
            if (depthLevel > result.UnlockedDepth)
            {
                result.CanEnter = false;
                result.LockReason = depthLevel == 1
                    ? "账号需先通关本地区普通副本"
                    : $"账号需要先通关深层 LV{depthLevel - 1}";
            }
            if (encounterCatalog is not null) result.Monsters = encounterCatalog.CreateMonsters(dungeon, depthLevel)
                .Select(monster => BuildMonsterPreview(dungeon, monster)).ToList();
            var definition = _depthCatalog.Find(dungeon.Code)!;
            if (depthLevel >= definition.ChallengeStartDepth)
                result.RewardPreview.Add(new DungeonRewardPreviewResponse
                {
                    Source = "挑战额外奖励", Kind = "Material", Code = definition.ChallengeFragmentCode,
                    Name = materialCatalog?.FindItem(definition.ChallengeFragmentCode)?.Name ?? definition.ChallengeFragmentCode,
                    Quantity = definition.ChallengeFragmentQuantity, ChancePercent = definition.ChallengeFragmentChancePercent
                });
        }
        return result;
    }

    public Task<(RoomDetailResponse? Detail, string? Error)> CreateRoomAsync(string monsterType, string? token) =>
        CreateRoomAsync(null, monsterType, token);

    public async Task<(RoomDetailResponse? Detail, string? Error)> CreateRoomAsync(int? dungeonId, string? legacyMonsterType, string? token, bool isRepeatBattle = false, bool isPreparationTimeoutEnabled = true, bool isPublic = false, int depthLevel = 1)
    {
        var (user, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        if (await CharacterActivityManager.IsBusyAsync(dbContext, character!.Id)) return (null, "CharacterAlreadyInRoom");

        await DbInitializer.EnsureDefaultDungeonsAsync(dbContext, worldCatalog);
        var dungeon = dungeonId.HasValue
            ? await dbContext.Dungeons.FindAsync(dungeonId.Value)
            : await dbContext.Dungeons.FirstOrDefaultAsync(item => item.MonsterName == legacyMonsterType) ?? await dbContext.Dungeons.OrderBy(item => item.SortOrder).FirstAsync();
        if (dungeon is null || dungeonId.HasValue && !dungeon.IsVisible) return (null, "DungeonNotFound");
        var depthError = await _depthProgress.AdmissionErrorAsync(user!.Id, dungeon, depthLevel);
        if (depthError is not null) return (null, depthError);
        character.Hp = TalentRules.EffectiveMaxHp(character);
        var monsters = (encounterCatalog?.CreateMonsters(dungeon, depthLevel) ??
            [new Monster { Name = dungeon.MonsterName, Element = dungeon.MonsterElement, Hp = dungeon.MonsterMaxHp, BaseMaxHp = dungeon.MonsterMaxHp, MaxHp = dungeon.MonsterMaxHp, Attack = dungeon.MonsterAttack, Defense = dungeon.MonsterDefense }]).ToList();
        var firstMonster = monsters.OrderBy(monster => monster.WaveNumber).ThenBy(monster => monster.Position).First();
        var now = DateTime.UtcNow;
        var room = new Room
        {
            DungeonId = dungeon.Id, MonsterId = 0, OwnerUserId = user!.Id, SlotCount = dungeon.SlotCount,
            DepthLevel = depthLevel,
            DepthDefinitionJson = _depthCatalog.Find(dungeon.Code) is { } depthDefinition ? JsonSerializer.Serialize(depthDefinition) : null,
            Status = RoomStatus.NotStarted, IsRepeatBattle = isRepeatBattle, IsPublic = isPublic,
            IsPreparationTimeoutEnabled = isPreparationTimeoutEnabled,
            PreparationStartedAtUtc = isPreparationTimeoutEnabled ? now : null,
            StartedAtUtc = now, ExpiresAtUtc = isRepeatBattle ? now.Add(MaximumActivityDuration) : null,
            CurrentWaveNumber = firstMonster.WaveNumber,
            TotalWaveCount = monsters.Max(monster => monster.WaveNumber)
        };
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        dbContext.Monsters.AddRange(monsters);
        await dbContext.SaveChangesAsync();
        room.MonsterId = firstMonster.Id;
        dbContext.Rooms.Add(room);
        await dbContext.SaveChangesAsync();
        foreach (var monster in monsters) monster.RoomId = room.Id;
        dbContext.RoomSlots.AddRange(Enumerable.Range(1, room.SlotCount).Select(index => new RoomSlot
        {
            RoomId = room.Id,
            SlotIndex = index,
            CharacterId = index == 1 ? character.Id : null,
            UserId = index == 1 ? user.Id : null,
            LastSeenAtUtc = index == 1 ? now : null
        }));
        CharacterActivityManager.StartBattle(dbContext, character.Id, room, now);
        try { await dbContext.SaveChangesAsync(); }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            await transaction.RollbackAsync();
            return (null, "CharacterAlreadyInRoom");
        }
        await _partyScaling.SynchronizeAsync(room);
        if (monsterCombatService is not null) await monsterCombatService.EnsureIntentAsync(room, firstMonster);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return (await BuildRoomDetailAsync(room, user.Id), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> JoinRoomAsync(int roomId, JoinRoomRequest request, string? token)
    {
        var (user, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        return await JoinRoomCoreAsync(roomId, request, user!, character!);
    }

    private async Task<(RoomDetailResponse? Detail, string? Error)> JoinRoomCoreAsync(int roomId, JoinRoomRequest request, User user, Character character)
    {
        var room = await dbContext.Rooms.FindAsync(roomId);
        if (room is null) return (null, "NotFound");
        if (room.ClosedAtUtc.HasValue) return (null, "RoomClosed");
        if (!room.IsPublic && room.OwnerUserId != user!.Id) return (null, "RoomPrivate");
        if (room.IsRepeatBattle && room.ExpiresAtUtc <= DateTime.UtcNow)
        {
            if (room.Status == RoomStatus.NotStarted && room.RoundNumber == 0)
            {
                await CharacterActivityManager.CloseBattleRoomAsync(dbContext, room, DateTime.UtcNow);
                room.Version++;
                await dbContext.SaveChangesAsync();
            }
            return (null, "RoomClosed");
        }
        var dungeon = await dbContext.Dungeons.FindAsync(room.DungeonId);
        if (dungeon is null) return (null, "DungeonNotFound");
        var depthError = await _depthProgress.AdmissionErrorAsync(user.Id, dungeon, room.DepthLevel);
        if (depthError is not null) return (null, depthError);
        if (room.OwnerUserId == user!.Id) return (null, "CannotJoinOwnRoom");
        if (room.Status == RoomStatus.BattleOver && (!room.IsRepeatBattle ||
            !await dbContext.Monsters.AnyAsync(monster => monster.Id == room.MonsterId && monster.Hp <= 0)))
            return (null, "BattleOver");
        if (room.Status is not (RoomStatus.NotStarted or RoomStatus.Preparing or RoomStatus.Cooldown or
            RoomStatus.WaveTransition or RoomStatus.BattleOver)) return (null, "RoomLocked");
        if (request.SlotIndex < 1 || request.SlotIndex > room.SlotCount) return (null, "InvalidSlotIndex");
        if (await dbContext.RoomSlots.AnyAsync(x => x.RoomId == roomId && x.UserId == user.Id)) return (null, "AlreadyInRoom");
        if (await CharacterActivityManager.IsBusyAsync(dbContext, character!.Id)) return (null, "CharacterAlreadyInRoom");
        var slot = await dbContext.RoomSlots.SingleAsync(x => x.RoomId == roomId && x.SlotIndex == request.SlotIndex);
        if (slot.CharacterId.HasValue) return (null, "SlotOccupied");
        var joinedAt = DateTime.UtcNow;
        character.Hp = TalentRules.EffectiveMaxHp(character);
        slot.CharacterId = character.Id;
        slot.UserId = user.Id;
        slot.LastSeenAtUtc = joinedAt;
        slot.HasParticipatedInRun = false;
        slot.LastParticipatedMonsterId = null;
        if (room.Status == RoomStatus.Preparing && room.IsPreparationTimeoutEnabled)
            room.PreparationStartedAtUtc = joinedAt;
        if (room.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc is DateTime cooldownDeadline &&
            room.RoundCooldownDurationSeconds == BattleRules.AutoRoundCooldownSeconds)
        {
            var manualDeadline = cooldownDeadline.AddSeconds(
                BattleRules.RoundCooldownSeconds - BattleRules.AutoRoundCooldownSeconds);
            room.RoundCooldownDurationSeconds = BattleRules.RoundCooldownSeconds;
            if (manualDeadline <= joinedAt)
            {
                room.Status = RoomStatus.NotStarted;
                room.NextRoundAvailableAtUtc = null;
                room.PreparationStartedAtUtc = room.IsPreparationTimeoutEnabled ? joinedAt : null;
            }
            else room.NextRoundAvailableAtUtc = manualDeadline;
        }
        CharacterActivityManager.StartBattle(dbContext, character.Id, room, joinedAt);
        await _partyScaling.SynchronizeAsync(room);
        room.Version++;
        try { await dbContext.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException)
        {
            return (null, "ConcurrencyConflict");
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            return (null, "CharacterAlreadyInRoom");
        }
        return (await BuildRoomDetailAsync(room, user.Id), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> SetRoomVisibilityAsync(int roomId, bool isPublic, string? token)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        var room = await dbContext.Rooms.FindAsync(roomId);
        if (room is null) return (null, "NotFound");
        if (room.OwnerUserId != user!.Id) return (null, "NotOwner");
        if (room.ClosedAtUtc.HasValue || room.IsRepeatBattle && room.ExpiresAtUtc <= DateTime.UtcNow)
            return (null, "RoomClosed");

        // Admission can change during a round without changing its combat or formation state.
        if (room.IsPublic != isPublic)
        {
            room.IsPublic = isPublic;
            room.Version++;
            try { await dbContext.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { return (null, "ConcurrencyConflict"); }
        }
        return (await BuildRoomDetailAsync(room, user.Id), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> LeaveRoomAsync(int roomId, string? token)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        return await LeaveRoomCoreAsync(roomId, user!);
    }

    private async Task<(RoomDetailResponse? Detail, string? Error)> LeaveRoomCoreAsync(int roomId, User user)
    {
        var room = await dbContext.Rooms.FindAsync(roomId);
        if (room is null) return (null, "NotFound");
        if (room.OwnerUserId == user!.Id) return (null, "NotRoomParticipant");
        if (room.Status != RoomStatus.BattleOver && (room.Status != RoomStatus.NotStarted || room.RoundNumber > 0)) return (null, "RoomLocked");
        var slot = await dbContext.RoomSlots.SingleOrDefaultAsync(x => x.RoomId == roomId && x.UserId == user.Id);
        if (slot is null) return (null, "NotRoomParticipant");
        var character = slot.CharacterId.HasValue ? await dbContext.Characters.FindAsync(slot.CharacterId.Value) : null;
        if (character is not null) character.Hp = TalentRules.EffectiveMaxHp(character);
        if (character is not null) await CharacterActivityManager.ReleaseBattleAsync(dbContext, character.Id, room.Id);
        slot.CharacterId = null;
        slot.UserId = null;
        slot.LastSeenAtUtc = null;
        slot.IsConfirmed = false;
        slot.IsAutoEnabled = false;
        slot.IsTemporaryAuto = false;
        slot.PendingConsumableSlotMask = 0;
        SkillQueueRules.Clear(slot);
        slot.IsSoulImprintQueued = false;
        slot.HasParticipatedInRun = false;
        slot.LastParticipatedMonsterId = null;
        await _partyScaling.SynchronizeAsync(room);
        room.Version++;
        await dbContext.SaveChangesAsync();
        return (await BuildRoomDetailAsync(room, user.Id), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> AssignSlotAsync(int roomId, AssignRoomSlotRequest request, string? token)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        return await AssignSlotCoreAsync(roomId, request, user!);
    }

    private async Task<(RoomDetailResponse? Detail, string? Error)> AssignSlotCoreAsync(int roomId, AssignRoomSlotRequest request, User user)
    {
        var (room, error) = await GetOwnerEditableRoomAsync(roomId, user);
        if (error is not null) return (null, error);
        if (request.SlotIndex < 1 || request.SlotIndex > room!.SlotCount) return (null, "InvalidSlotIndex");
        var character = await dbContext.Characters.FirstOrDefaultAsync(x => x.Id == request.CharacterId);
        if (character is null) return (null, "CharacterNotFound");
        if (character.UserId != user!.Id) return (null, "NotCharacterOwner");
        var dungeon = await dbContext.Dungeons.FindAsync(room!.DungeonId);
        if (dungeon is null) return (null, "DungeonNotFound");
        var depthError = await _depthProgress.AdmissionErrorAsync(user.Id, dungeon, room.DepthLevel);
        if (depthError is not null) return (null, depthError);
        var existingSlot = await dbContext.RoomSlots.FirstOrDefaultAsync(x => x.CharacterId == character.Id);
        if (existingSlot is null && await dbContext.CharacterActivities.AnyAsync(activity => activity.CharacterId == character.Id))
            return (null, "CharacterAlreadyInRoom");
        if (existingSlot is not null && existingSlot.RoomId != room!.Id) return (null, "CharacterAlreadyInRoom");
        var target = await dbContext.RoomSlots.SingleAsync(x => x.RoomId == room!.Id && x.SlotIndex == request.SlotIndex);
        if (existingSlot is not null)
        {
            if (existingSlot.Id == target.Id) return (await BuildRoomDetailAsync(room!, user.Id), null);
            if (target.CharacterId.HasValue && target.UserId != user.Id) return (null, "NotCharacterOwner");
            return await MoveSlotAsync(room!, user.Id, existingSlot, target);
        }
        if (target.CharacterId.HasValue && target.CharacterId != character.Id) return (null, "SlotOccupied");
        character.Hp = TalentRules.EffectiveMaxHp(character);
        target.CharacterId = character.Id;
        target.UserId = user.Id;
        target.IsAutoEnabled = room!.IsOwnerAutoEnabled;
        target.LastSeenAtUtc = DateTime.UtcNow;
        if (existingSlot is null)
        {
            target.HasParticipatedInRun = false;
            target.LastParticipatedMonsterId = null;
        }
        if (existingSlot is null) CharacterActivityManager.StartBattle(dbContext, character.Id, room, DateTime.UtcNow);
        target.PendingConsumableSlotMask = 0;
        SkillQueueRules.Clear(target);
        target.IsSoulImprintQueued = false;
        await _partyScaling.SynchronizeAsync(room!);
        room!.Version++;
        try { await dbContext.SaveChangesAsync(); }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            return (null, "CharacterAlreadyInRoom");
        }
        return (await BuildRoomDetailAsync(room, user.Id), null);
    }

    private async Task<(RoomDetailResponse? Detail, string? Error)> MoveSlotAsync(
        Room room, int userId, RoomSlot source, RoomSlot target)
    {
        var sourceCharacterId = source.CharacterId;
        var targetCharacterId = target.CharacterId;
        await using var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync() : null;
        try
        {
            // Release the unique character keys before exchanging occupants. Both saves are atomic.
            source.CharacterId = null;
            target.CharacterId = null;
            room.Version++;
            await dbContext.SaveChangesAsync();

            source.CharacterId = targetCharacterId;
            target.CharacterId = sourceCharacterId;
            (source.UserId, target.UserId) = (target.UserId, source.UserId);
            (source.LastSeenAtUtc, target.LastSeenAtUtc) = (target.LastSeenAtUtc, source.LastSeenAtUtc);
            (source.IsConfirmed, target.IsConfirmed) = (target.IsConfirmed, source.IsConfirmed);
            (source.IsAutoEnabled, target.IsAutoEnabled) = (target.IsAutoEnabled, source.IsAutoEnabled);
            (source.IsTemporaryAuto, target.IsTemporaryAuto) = (target.IsTemporaryAuto, source.IsTemporaryAuto);
            (source.PendingConsumableSlotMask, target.PendingConsumableSlotMask) =
                (target.PendingConsumableSlotMask, source.PendingConsumableSlotMask);
            (source.PendingSkillSlotMask, target.PendingSkillSlotMask) = (target.PendingSkillSlotMask, source.PendingSkillSlotMask);
            (source.IsSoulImprintQueued, target.IsSoulImprintQueued) = (target.IsSoulImprintQueued, source.IsSoulImprintQueued);
            (source.HasParticipatedInRun, target.HasParticipatedInRun) = (target.HasParticipatedInRun, source.HasParticipatedInRun);
            (source.LastParticipatedMonsterId, target.LastParticipatedMonsterId) =
                (target.LastParticipatedMonsterId, source.LastParticipatedMonsterId);
            await dbContext.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) { await transaction.RollbackAsync(); dbContext.ChangeTracker.Clear(); }
            return (null, "ConcurrencyConflict");
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            if (transaction is not null) { await transaction.RollbackAsync(); dbContext.ChangeTracker.Clear(); }
            return (null, "CharacterAlreadyInRoom");
        }
        return (await BuildRoomDetailAsync(room, userId), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> RemoveSlotAsync(int roomId, int slotIndex, string? token)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        return await RemoveSlotCoreAsync(roomId, slotIndex, user!);
    }

    private async Task<(RoomDetailResponse? Detail, string? Error)> RemoveSlotCoreAsync(int roomId, int slotIndex, User user)
    {
        var (room, error) = await GetOwnerEditableRoomAsync(roomId, user);
        if (error is not null) return (null, error);
        if (slotIndex < 1 || slotIndex > room!.SlotCount) return (null, "InvalidSlotIndex");
        var slot = await dbContext.RoomSlots.SingleAsync(x => x.RoomId == room!.Id && x.SlotIndex == slotIndex);
        if (slot.UserId != user!.Id) return (null, "NotCharacterOwner");
        var character = slot.CharacterId.HasValue ? await dbContext.Characters.FindAsync(slot.CharacterId.Value) : null;
        if (character is not null) character.Hp = TalentRules.EffectiveMaxHp(character);
        if (character is not null) await CharacterActivityManager.ReleaseBattleAsync(dbContext, character.Id, room.Id);
        slot.CharacterId = null;
        slot.UserId = null;
        slot.LastSeenAtUtc = null;
        slot.IsConfirmed = false;
        slot.IsAutoEnabled = false;
        slot.IsTemporaryAuto = false;
        slot.PendingConsumableSlotMask = 0;
        SkillQueueRules.Clear(slot);
        slot.IsSoulImprintQueued = false;
        slot.HasParticipatedInRun = false;
        slot.LastParticipatedMonsterId = null;
        await _partyScaling.SynchronizeAsync(room!);
        room.Version++;
        await dbContext.SaveChangesAsync();
        return (await BuildRoomDetailAsync(room, user!.Id), null);
    }

    public async Task<(bool Success, string? Error)> DeleteRoomAsync(int roomId, string? token)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null) return (false, "NotFound");
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (false, error);
        if (room.OwnerUserId != user!.Id) return (false, "NotOwner");
        if (await dbContext.RewardRuns.AnyAsync(run => run.RoomId == roomId && run.Sequence == room.RunSequence && run.Status == "Pending"))
            await rewardService.SettleAsync(room, false, DateTime.UtcNow, []);
        var roomSlots = await dbContext.RoomSlots.Where(x => x.RoomId == roomId).ToListAsync();
        var characterIds = roomSlots.Where(slot => slot.CharacterId.HasValue).Select(slot => slot.CharacterId!.Value).ToList();
        dbContext.CharacterActivities.RemoveRange(await dbContext.CharacterActivities
            .Where(activity => activity.Kind == CharacterActivityManager.BattleKind && activity.SourceId == roomId).ToListAsync());
        var characters = await dbContext.Characters.Where(character => characterIds.Contains(character.Id)).ToListAsync();
        foreach (var character in characters) character.Hp = TalentRules.EffectiveMaxHp(character);
        dbContext.RoomSlots.RemoveRange(roomSlots);
        dbContext.BattleConsumableCooldowns.RemoveRange(await dbContext.BattleConsumableCooldowns.Where(x => x.RoomId == roomId).ToListAsync());
        dbContext.BattleOperationPotionStates.RemoveRange(await dbContext.BattleOperationPotionStates.Where(x => x.RoomId == roomId).ToListAsync());
        dbContext.BattleHealingPotionStates.RemoveRange(await dbContext.BattleHealingPotionStates.Where(x => x.RoomId == roomId).ToListAsync());
        dbContext.BattleConsumableBuffs.RemoveRange(await dbContext.BattleConsumableBuffs.Where(x => x.RoomId == roomId).ToListAsync());
        dbContext.BattleSkillCooldowns.RemoveRange(await dbContext.BattleSkillCooldowns.Where(x => x.RoomId == roomId).ToListAsync());
        dbContext.MonsterIntents.RemoveRange(await dbContext.MonsterIntents.Where(x => x.RoomId == roomId).ToListAsync());
        dbContext.BattleStatusEffects.RemoveRange(await dbContext.BattleStatusEffects.Where(x => x.RoomId == roomId).ToListAsync());
        dbContext.BattleMonsterSkillCooldowns.RemoveRange(await dbContext.BattleMonsterSkillCooldowns.Where(x => x.RoomId == roomId).ToListAsync());
        var monsters = await dbContext.Monsters.Where(monster => monster.RoomId == roomId).ToListAsync();
        var monster = await dbContext.Monsters.FindAsync(room.MonsterId);
        dbContext.Rooms.Remove(room);
        if (monsters.Count > 0) dbContext.Monsters.RemoveRange(monsters);
        else if (monster is not null) dbContext.Monsters.Remove(monster);
        await dbContext.SaveChangesAsync();
        battleLogStore?.Clear(roomId);
        return (true, null);
    }

    private async Task<(Room? Room, string? Error)> GetOwnerEditableRoomAsync(int roomId, User user)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null) return (null, "NotFound");
        if (room.OwnerUserId != user.Id) return (room, "NotOwner");
        if (room.ClosedAtUtc.HasValue) return (room, "RoomClosed");
        if (room.IsRepeatBattle && room.ExpiresAtUtc <= DateTime.UtcNow &&
            (room.Status == RoomStatus.BattleOver || room.Status == RoomStatus.NotStarted && room.RoundNumber == 0))
        {
            await CharacterActivityManager.CloseBattleRoomAsync(dbContext, room, DateTime.UtcNow);
            room.Version++;
            await dbContext.SaveChangesAsync();
            return (room, "RoomClosed");
        }
        if (room.Status != RoomStatus.BattleOver && (room.Status != RoomStatus.NotStarted || room.RoundNumber > 0)) return (room, "FormationLocked");
        return (room, null);
    }

    private async Task<RoomDetailResponse?> BuildRoomDetailAsync(Room room, int? currentUserId)
    {
        var monster = await dbContext.Monsters.FindAsync(room.MonsterId);
        var dungeon = await dbContext.Dungeons.FindAsync(room.DungeonId);
        if (monster is null || dungeon is null) return null;
        var enemiesInCurrentWave = monster.RoomId.HasValue
            ? await dbContext.Monsters.CountAsync(candidate => candidate.RoomId == room.Id && candidate.WaveNumber == monster.WaveNumber)
            : 1;
        var slots = await dbContext.RoomSlots.Where(x => x.RoomId == room.Id).OrderBy(x => x.SlotIndex).ToListAsync();
        var characterIds = slots.Where(x => x.CharacterId.HasValue).Select(x => x.CharacterId!.Value).ToList();
        var characters = await dbContext.Characters.Where(x => characterIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var activeConsumableBuffs = room.Status == RoomStatus.BattleOver || room.ClosedAtUtc.HasValue
            ? []
            : await dbContext.BattleConsumableBuffs.Where(buff => buff.RoomId == room.Id &&
                buff.RunSequence == room.RunSequence && buff.ExpiresAfterRound >= room.RoundNumber &&
                characterIds.Contains(buff.CharacterId)).ToListAsync();
        if (weaponCatalog is not null && activeConsumableBuffs.Count > 0)
        {
            var equippedWeapons = await dbContext.CharacterWeapons.Include(weapon => weapon.Skills)
                .Where(weapon => characterIds.Contains(weapon.CharacterId) && weapon.EquippedSlotIndex != null).ToListAsync();
            foreach (var character in characters.Values)
            {
                var levels = activeConsumableBuffs.Where(buff => buff.CharacterId == character.Id)
                    .GroupBy(buff => buff.WeaponSkillCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Sum(buff => buff.SkillLevel), StringComparer.OrdinalIgnoreCase);
                if (levels.Count > 0)
                    BattleConsumableBonusCalculator.Apply(character, weaponCatalog.CalculateBonuses(
                        equippedWeapons.Where(weapon => weapon.CharacterId == character.Id), levels));
            }
        }
        var mainWeaponElements = await dbContext.CharacterWeapons
            .Where(weapon => characterIds.Contains(weapon.CharacterId) && weapon.EquippedSlotIndex == WeaponRules.MainSlotIndex)
            .ToDictionaryAsync(weapon => weapon.CharacterId, weapon => weapon.Element);
        var userIds = slots.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).ToList();
        var users = await dbContext.Users.Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var now = DateTime.UtcNow;
        var aliveSlots = slots.Where(x => x.CharacterId.HasValue && characters.TryGetValue(x.CharacterId.Value, out var character) && character.Hp > 0).ToList();
        var currentUserAliveSlots = aliveSlots.Where(x => x.UserId == currentUserId).ToList();
        var clearedDungeonCharacterIds = await dbContext.CharacterBattleMilestones.Where(milestone =>
                milestone.Kind == BattleMilestoneService.DungeonClearKind &&
                milestone.TargetCode == dungeon.Code && milestone.Count > 0)
            .Select(milestone => milestone.CharacterId).ToListAsync();
        var currentUserCharacterIds = slots.Where(slot => slot.UserId == currentUserId && slot.CharacterId.HasValue)
            .Select(slot => slot.CharacterId!.Value).ToList();
        var consumableSlots = await dbContext.CharacterConsumableSlots
            .Where(slot => currentUserCharacterIds.Contains(slot.CharacterId)).ToListAsync();
        var itemStacks = await dbContext.CharacterItemStacks
            .Where(stack => currentUserCharacterIds.Contains(stack.CharacterId)).ToListAsync();
        var cooldowns = await dbContext.BattleConsumableCooldowns
            .Where(entry => entry.RoomId == room.Id && currentUserCharacterIds.Contains(entry.CharacterId)).ToListAsync();
        var operationPotionStates = await dbContext.BattleOperationPotionStates
            .Where(state => state.RoomId == room.Id && state.RunSequence == room.RunSequence &&
                characterIds.Contains(state.CharacterId)).ToListAsync();
        var healingPotionStates = await dbContext.BattleHealingPotionStates
            .Where(state => state.RoomId == room.Id && state.RunSequence == room.RunSequence &&
                currentUserCharacterIds.Contains(state.CharacterId)).ToDictionaryAsync(state => state.CharacterId);
        var skillSlots = await dbContext.CharacterSkillSlots
            .Where(entry => currentUserCharacterIds.Contains(entry.CharacterId)).ToListAsync();
        var skillCooldowns = await dbContext.BattleSkillCooldowns
            .Where(entry => entry.RoomId == room.Id && currentUserCharacterIds.Contains(entry.CharacterId)).ToListAsync();
        var equippedSoulImprints = soulImprintCatalog is null ? [] : await dbContext.CharacterSoulImprints
            .Where(entry => currentUserCharacterIds.Contains(entry.CharacterId) &&
                entry.EquippedSlotIndex == SoulImprintRules.SlotIndex).ToListAsync();
        var professionLevelsByCharacter = (await dbContext.CharacterCombatProfessions
            .Where(entry => currentUserCharacterIds.Contains(entry.CharacterId)).ToListAsync())
            .GroupBy(entry => entry.CharacterId)
            .ToDictionary(group => group.Key, group => group.ToDictionary(entry => entry.ProfessionCode,
                entry => entry.Level, StringComparer.OrdinalIgnoreCase));
        var isCurrentUserAutoUnlocked = currentUserAliveSlots.Any(slot =>
            slot.CharacterId.HasValue && clearedDungeonCharacterIds.Contains(slot.CharacterId.Value));
        var currentUserSlot = slots.FirstOrDefault(slot => slot.UserId == currentUserId && slot.CharacterId.HasValue);
        var isCurrentUserAutoEnabled = currentUserId == room.OwnerUserId ? room.IsOwnerAutoEnabled :
            currentUserSlot is not null && RoomAutoPolicy.IsEnabled(room, currentUserSlot, slots);
        var isMixedTeam = slots.Any(slot => slot.UserId.HasValue && slot.UserId != room.OwnerUserId);
        var isPreparationTimeoutEnabled = room.IsPreparationTimeoutEnabled;
        var isAllAliveMembersAuto = aliveSlots.Count > 0 && aliveSlots.All(slot =>
            RoomAutoPolicy.IsAuto(room, slot, clearedDungeonCharacterIds, slots));
        var rewardRuns = currentUserId.HasValue
            ? await dbContext.RewardRuns.Where(run => run.RoomId == room.Id && run.Sequence <= room.RunSequence)
                .OrderByDescending(run => run.Sequence).ToListAsync()
            : [];
        var rewardRun = rewardRuns.FirstOrDefault();
        var pendingSequences = rewardRuns.Where(run => run.Status == "Pending").Select(run => run.Sequence).ToList();
        var rewardEntries = rewardRun is null ? [] : await dbContext.RewardEntries.AsNoTracking()
            .Where(entry => entry.RoomId == room.Id && entry.Sequence == rewardRun.Sequence && entry.UserId == currentUserId)
            .OrderBy(entry => entry.Id).ToListAsync();
        var cumulativeEntries = currentUserId.HasValue ? await dbContext.RewardEntries.AsNoTracking()
            .Where(entry => entry.RoomId == room.Id && entry.Sequence <= room.RunSequence && entry.UserId == currentUserId.Value)
            .GroupBy(entry => new { entry.CharacterId, entry.Kind, entry.Code, entry.WeaponSnapshotJson, entry.RewardSource })
            .Select(group => new CumulativeRewardEntry
            {
                CharacterId = group.Key.CharacterId, Kind = group.Key.Kind, Code = group.Key.Code,
                WeaponSnapshotJson = group.Key.WeaponSnapshotJson,
                RewardSource = group.Key.RewardSource,
                Quantity = group.Sum(entry => entry.Quantity),
                PendingQuantity = group.Sum(entry => pendingSequences.Contains(entry.Sequence) ? entry.Quantity : 0)
            }).ToListAsync()
            : [];
        var rewardCharacterIds = cumulativeEntries.Select(entry => entry.CharacterId)
            .Concat(currentUserCharacterIds).Distinct().ToList();
        var rewardCharacters = await dbContext.Characters.Where(character => rewardCharacterIds.Contains(character.Id))
            .ToDictionaryAsync(character => character.Id, character => character.Name);
        var monsterIntent = monsterCombatService is null ? null : await monsterCombatService.GetIntentResponseAsync(room, monster);
        var monsterEffects = monsterCombatService is null ? [] : await monsterCombatService.GetStatusResponsesAsync(room, "Monster", monster.Id);
        var characterEffects = new Dictionary<int, List<BattleStatusEffectResponse>>();
        if (monsterCombatService is not null)
        {
            foreach (var characterId in characterIds)
                characterEffects[characterId] = await monsterCombatService.GetStatusResponsesAsync(room, "Character", characterId);
            if (dbContext.ChangeTracker.Entries<MonsterIntent>().Any(entry =>
                    entry.State is EntityState.Added or EntityState.Modified))
                await dbContext.SaveChangesAsync();
        }
        var activeCharacterId = currentUserId.HasValue ? (await dbContext.Users.FindAsync(currentUserId.Value))?.ActiveCharacterId : null;
        var definition = await _depthProgress.DefinitionAsync(room);
        var highestDepth = definition is not null && activeCharacterId.HasValue
            ? await _depthProgress.HighestAsync(activeCharacterId.Value, dungeon.Id) : 0;
        return new RoomDetailResponse
        {
            RoomId = room.Id, OwnerUserId = room.OwnerUserId, DungeonId = dungeon.Id,
            DungeonName = definition is null ? dungeon.Name : _depthCatalog.DisplayName(dungeon.Name, room.DepthLevel), SlotCount = room.SlotCount,
            SupportsDepths = definition is not null, DepthLevel = room.DepthLevel,
            CharacterHighestDepth = highestDepth, MasteryLevel = Math.Min(4, highestDepth),
            UnlockedDepth = definition is not null && currentUserId.HasValue ? await _depthProgress.UnlockedAsync(currentUserId.Value, dungeon.Id, definition.MaximumDepth) : 1,
            Operations = await GetOperationResponsesAsync(room.Id, currentUserId),
            RegionName = dungeon.RegionName,
            MonsterName = monster.Name, MonsterElement = monster.Element, MonsterHp = monster.Hp, MonsterMaxHp = monster.MaxHp,
            MonsterBaseMaxHp = monster.BaseMaxHp > 0 ? monster.BaseMaxHp : monster.MaxHp,
            IsPartyHpScaled = _partyScaling.Catalog.ScalesHp(dungeon.PartyScalingProfileCode),
            ScalingPartySize = room.ScalingPartySize,
            MonsterHpPercent = _partyScaling.Catalog.GetHpPercent(dungeon.PartyScalingProfileCode, room.ScalingPartySize),
            CurrentWaveNumber = room.CurrentWaveNumber, TotalWaveCount = room.TotalWaveCount,
            CurrentEnemyNumber = monster.Position, EnemiesInCurrentWave = enemiesInCurrentWave,
            RoomStatus = room.Status, RunSequence = room.RunSequence, RoundNumber = room.RoundNumber, NextRoundAvailableAtUtc = room.NextRoundAvailableAtUtc, RoundCooldownDurationSeconds = room.RoundCooldownDurationSeconds, PreparationStartedAtUtc = room.PreparationStartedAtUtc, PreparationExpiresAtUtc = isPreparationTimeoutEnabled ? room.PreparationStartedAtUtc?.AddSeconds(BattleRules.PreparationTimeoutSeconds) : null, BattleEndedAtUtc = room.BattleEndedAtUtc,
            IsRepeatBattle = room.IsRepeatBattle, IsPublic = room.IsPublic, StartedAtUtc = room.StartedAtUtc,
            ExpiresAtUtc = room.ExpiresAtUtc, ClosedAtUtc = room.ClosedAtUtc,
            NextBattleStartAtUtc = room.ClosedAtUtc is null && room.IsRepeatBattle && room.Status == RoomStatus.BattleOver && monster.Hp <= 0 ? room.BattleEndedAtUtc?.AddSeconds(BattleRules.RepeatBattleDelaySeconds) : null,
            NextWaveStartAtUtc = room.Status == RoomStatus.WaveTransition ? room.NextRoundAvailableAtUtc : null,
            ServerTimeUtc = now,
            CanExecuteRound = room.Status == RoomStatus.Preparing && aliveSlots.Count > 0 && aliveSlots.All(x => x.IsConfirmed) && monster.Hp > 0,
            IsMixedTeam = isMixedTeam, IsPreparationTimeoutEnabled = isPreparationTimeoutEnabled, PreparationTimeoutSeconds = BattleRules.PreparationTimeoutSeconds, IsCurrentUserAutoUnlocked = isCurrentUserAutoUnlocked, IsAllAliveMembersAuto = isAllAliveMembersAuto,
            IsCurrentUserAutoEnabled = isCurrentUserAutoEnabled,
            CanPrepare = room.ClosedAtUtc is null && currentUserAliveSlots.Any(slot => !slot.IsConfirmed &&
                    !RoomAutoPolicy.IsAuto(room, slot, clearedDungeonCharacterIds, slots)) && !isAllAliveMembersAuto && monster.Hp > 0 &&
                room.Status is RoomStatus.NotStarted or RoomStatus.Preparing or RoomStatus.Cooldown,
            CanCancelPreparation = room.ClosedAtUtc is null && monster.Hp > 0 &&
                (room.Status is RoomStatus.NotStarted or RoomStatus.Preparing or RoomStatus.Cooldown) &&
                currentUserAliveSlots.Any(slot => slot.IsConfirmed &&
                    !RoomAutoPolicy.IsAuto(room, slot, clearedDungeonCharacterIds, slots)),
            CanLeaveRoom = room.ClosedAtUtc is null && currentUserId.HasValue && currentUserId != room.OwnerUserId &&
                (room.Status == RoomStatus.BattleOver || room.Status == RoomStatus.NotStarted && room.RoundNumber == 0) &&
                slots.Any(x => x.UserId == currentUserId),
            MonsterIntent = monsterIntent,
            MonsterEffects = monsterEffects,
            BattleLogs = battleLogStore?.Get(room.Id) ?? [],
            CumulativeRewards = currentUserId.HasValue
                ? BuildCumulativeRewards(cumulativeEntries, rewardRuns, rewardCharacters, currentUserCharacterIds)
                : null,
            Rewards = rewardRun is null || rewardRun.Sequence < room.RunSequence && rewardEntries.Count == 0
                ? null : new RoomRewardSummaryResponse
            {
                RunSequence = rewardRun.Sequence, IsCurrentRun = rewardRun.Sequence == room.RunSequence,
                Status = rewardRun.Status, SettledAtUtc = rewardRun.SettledAtUtc,
                Gold = rewardEntries.Where(entry => entry.Kind == "Gold").Sum(entry => entry.Quantity),
                MasteryGold = rewardEntries.Where(entry => entry.Kind == "Gold" && entry.RewardSource == "Mastery").Sum(entry => entry.Quantity),
                Experience = rewardEntries.Where(entry => entry.Kind == "Experience").Sum(entry => entry.Quantity),
                Items = rewardEntries.Where(entry => entry.Kind is "Consumable" or "Material" or "Weapon" or "SoulImprint")
                    .Select(entry => new RoomRewardItemResponse
                    {
                        CharacterId = entry.CharacterId, Code = entry.Code,
                        CharacterName = rewardCharacters.GetValueOrDefault(entry.CharacterId, "角色"),
                        Kind = entry.Kind, Quantity = entry.Quantity,
                        Source = RewardSourceName(entry.RewardSource, entry.EventKey == "clear" ? "通关" : entry.EventKey == "first-clear" ? "首通" : "击杀"),
                        Name = DescribeRewardItem(entry)
                    }).ToList()
            },
            Slots = slots.Select(slot =>
            {
                characters.TryGetValue(slot.CharacterId ?? 0, out var character);
                users.TryGetValue(slot.UserId ?? 0, out var player);
                var ownCharacterId = slot.UserId == currentUserId ? slot.CharacterId : null;
                var healingUses = ownCharacterId is int healingCharacterId
                    ? healingPotionStates.GetValueOrDefault(healingCharacterId)?.UsesUsed ?? 0 : 0;
                var ownProfessionLevels = ownCharacterId is int professionCharacterId &&
                    professionLevelsByCharacter.TryGetValue(professionCharacterId, out var levels)
                    ? levels : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var ownConsumables = ownCharacterId is int id
                    ? Enumerable.Range(1, ConsumableRules.SlotCount).Select(index =>
                    {
                        var equipped = consumableSlots.FirstOrDefault(entry => entry.CharacterId == id && entry.SlotIndex == index);
                        var item = consumableCatalog.FindItem(equipped?.ItemCode);
                        var cooldown = item is null ? null : cooldowns.FirstOrDefault(entry => entry.CharacterId == id && entry.CooldownGroup == item.CooldownGroup);
                        return new RoomConsumableSlotResponse
                        {
                            SlotIndex = index,
                            ItemCode = item?.Code,
                            ItemName = item?.Name,
                            Kind = item?.Kind,
                            Description = item is null || character is null ? null : ConsumableCatalog.Description(item, character.Level),
                            HealAmount = item is null || character is null || item.Kind != "Healing" ? 0 : ConsumableCatalog.HealAmountFor(item, TalentRules.EffectiveMaxHp(character), character.Level),
                            Quantity = item is null ? 0 : itemStacks.FirstOrDefault(stack => stack.CharacterId == id && stack.ItemCode == item.Code)?.Quantity ?? 0,
                            CooldownRoundsRemaining = Math.Max(0, (cooldown?.ReadyAtRound ?? 0) - room.RoundNumber),
                            AutoUseEnabled = equipped?.AutoUseEnabled ?? false,
                            AutoHpThresholdPercent = equipped?.AutoHpThresholdPercent ?? ConsumableRules.DefaultAutoHpThresholdPercent,
                            UnavailableReason = character is null ? "CharacterDead" : ConsumableUsePolicy.UnavailableReason(
                                room, character, index, item,
                                item is null ? 0 : itemStacks.FirstOrDefault(stack => stack.CharacterId == id && stack.ItemCode == item.Code)?.Quantity ?? 0,
                                Math.Max(0, (cooldown?.ReadyAtRound ?? 0) - room.RoundNumber), healingUses,
                                item?.Kind == "CombatBuff" && activeConsumableBuffs.Any(buff => buff.CharacterId == id && buff.WeaponSkillCode == item.WeaponSkillCode))
                        };
                    }).ToList()
                    : [];
                RoomOperationPotionResponse? operationPotion = null;
                if (ownCharacterId is int potionCharacterId)
                {
                    var equippedPotion = consumableSlots.FirstOrDefault(entry =>
                        entry.CharacterId == potionCharacterId && entry.SlotIndex == ConsumableRules.OperationPotionSlotIndex);
                    var item = consumableCatalog.FindItem(equippedPotion?.ItemCode);
                    var state = operationPotionStates.FirstOrDefault(entry => entry.CharacterId == potionCharacterId);
                    operationPotion = new RoomOperationPotionResponse
                    {
                        ItemCode = item?.Code,
                        ItemName = item?.Name,
                        Description = item is null || character is null ? null : state?.ItemCode is not null
                            ? ConsumableCatalog.ActiveOperationDescription(state) : ConsumableCatalog.Description(item, character.Level),
                        Quantity = item is null ? 0 : itemStacks.FirstOrDefault(stack =>
                            stack.CharacterId == potionCharacterId && stack.ItemCode == item.Code)?.Quantity ?? 0,
                        AttackPercent = state?.AttackPercent > 0 ? state.AttackPercent : item?.AttackPercent ?? 0,
                        HasAttempted = state is not null,
                        IsActive = state?.ItemCode is not null && room.Status != RoomStatus.BattleOver && room.ClosedAtUtc is null
                    };
                }
                var ownSkills = ownCharacterId is int skillCharacterId
                    ? Enumerable.Range(1, SkillRules.SlotCount).Select(index =>
                    {
                        var equipped = skillSlots.FirstOrDefault(entry => entry.CharacterId == skillCharacterId && entry.SlotIndex == index);
                        var skill = character is null ? null :
                            skillCatalog.ResolveSkillForLevel(character, equipped?.SkillCode, ownProfessionLevels);
                        var cooldown = skill is null ? null : skillCooldowns.FirstOrDefault(entry => entry.CharacterId == skillCharacterId && entry.SkillCode == skill.Code);
                        return new RoomSkillSlotResponse
                        {
                            SlotIndex = index,
                            SkillCode = skill?.Code,
                            QueuedTargetCharacterId = skill is null ? null : SkillQueueRules.TargetCharacterId(slot, index),
                            SkillName = skill?.Name,
                            Description = skill?.Description,
                            EffectType = skill is null ? null : SkillCatalog.PrimaryEffectType(skill),
                            Power = skill is null ? 0 : SkillCatalog.PrimaryPower(skill),
                            AutoCondition = skill is null ? "Always" : equipped?.AutoConditionOverride ?? SkillCatalog.AutoConditionFor(skill),
                            AutoConditionOverride = skill is null ? null : equipped?.AutoConditionOverride,
                            Effects = skill is null ? [] : SkillCatalog.EffectsFor(skill).Select(effect => new SkillEffectResponse
                            {
                                Type = effect.Type, Target = effect.Target, Power = effect.Power,
                                StatusCode = effect.StatusCode, DurationRounds = effect.DurationRounds
                            }).ToList(),
                            InitialCooldownRounds = skill?.InitialCooldownRounds ?? 0,
                            CooldownRoundsRemaining = Math.Max(0,
                                (cooldown?.ReadyAtRound ?? skill?.InitialCooldownRounds ?? 0) - room.RoundNumber),
                            AutoUseEnabled = skill is not null && equipped?.AutoUseEnabled == true,
                            AutoHpThresholdPercent = equipped?.AutoHpThresholdPercent ?? SkillRules.DefaultAutoHpThresholdPercent
                        };
                    }).ToList()
                    : [];
                RoomSoulImprintResponse? ownSoulImprint = null;
                if (ownCharacterId is int soulCharacterId && soulImprintCatalog is not null)
                {
                    var equipped = equippedSoulImprints.SingleOrDefault(entry => entry.CharacterId == soulCharacterId);
                    var definition = soulImprintCatalog.Find(equipped?.SoulImprintCode);
                    if (equipped is not null && definition is not null)
                    {
                        var cooldownCode = SoulImprintRules.CooldownCode(definition.Code);
                        var cooldown = skillCooldowns.FirstOrDefault(entry => entry.CharacterId == soulCharacterId &&
                            entry.SkillCode == cooldownCode);
                        var readyAtRound = cooldown?.ReadyAtRound ?? definition.InitialCooldownRounds;
                        ownSoulImprint = new RoomSoulImprintResponse
                        {
                            Id = equipped.Id, Code = definition.Code, Name = definition.Name,
                            Description = definition.Description, Element = definition.Element,
                            EffectType = definition.EffectType, PowerPercent = definition.PowerPercent,
                            SecondaryPowerPercent = definition.SecondaryPowerPercent,
                            DurationRounds = definition.DurationRounds,
                            InitialCooldownRounds = definition.InitialCooldownRounds,
                            CooldownRounds = definition.CooldownRounds,
                            CooldownRoundsRemaining = Math.Max(0, readyAtRound - room.RoundNumber),
                            AutoUseEnabled = equipped.AutoUseEnabled
                        };
                    }
                }
                var characterElement = slot.CharacterId is int characterId && mainWeaponElements.TryGetValue(characterId, out var element)
                    ? element : (ElementType?)null;
                var isAutoUnlocked = slot.UserId == currentUserId && slot.CharacterId.HasValue &&
                    clearedDungeonCharacterIds.Contains(slot.CharacterId.Value);
                var canConfigureAuto = room.ClosedAtUtc is null && slot.UserId == currentUserId &&
                    slot.CharacterId.HasValue && (isCurrentUserAutoUnlocked || isCurrentUserAutoEnabled);
                var statusEffects = character is null
                    ? new List<BattleStatusEffectResponse>()
                    : (characterEffects.GetValueOrDefault(character.Id) ?? []).ToList();
                if (character is not null)
                    foreach (var buff in activeConsumableBuffs.Where(buff => buff.CharacterId == character.Id))
                    {
                        var item = consumableCatalog.FindItem(buff.ItemCode);
                        statusEffects.Insert(0, new BattleStatusEffectResponse
                        {
                            Code = $"combat-consumable:{buff.ItemCode}", Name = item?.Name ?? "战斗药剂",
                            Description = $"{ConsumableCatalog.WeaponSkillName(buff.WeaponSkillCode)} Lv{buff.SkillLevel} · 本场剩余 {buff.ExpiresAfterRound - room.RoundNumber + 1} 回合",
                            IsPositive = true, CanDispel = false, Stacks = 1,
                            RemainingRounds = buff.ExpiresAfterRound - room.RoundNumber + 1,
                            ExpiresWithRun = true
                        });
                    }
                var activePotion = character is null || room.Status == RoomStatus.BattleOver || room.ClosedAtUtc.HasValue
                    ? null
                    : operationPotionStates.FirstOrDefault(state => state.CharacterId == character.Id && state.ItemCode is not null);
                if (activePotion is not null)
                {
                    var potionName = consumableCatalog.FindItem(activePotion.ItemCode)?.Name ?? "作战药剂";
                    statusEffects.Insert(0, new BattleStatusEffectResponse
                    {
                        Code = $"operation-potion:{activePotion.ItemCode}",
                        Name = potionName,
                        Description = ConsumableCatalog.ActiveOperationDescription(activePotion),
                        IsPositive = true,
                        CanDispel = false,
                        Stacks = 1,
                        RemainingRounds = 999,
                        ExpiresWithRun = true
                    });
                }
                var isAuto = RoomAutoPolicy.IsAuto(room, slot, clearedDungeonCharacterIds, slots);
                var isOffline = RoomAutoPolicy.IsOffline(room, slot, now);
                return new RoomSlotResponse { SlotIndex = slot.SlotIndex, CharacterId = slot.CharacterId, PendingConsumableSlotMask = ownCharacterId.HasValue ? slot.PendingConsumableSlotMask : 0, HealingPotionUsesUsed = healingUses, HealingPotionUsesRemaining = ownCharacterId.HasValue ? Math.Max(0, ConsumableRules.HealingPotionUsesPerRun - healingUses) : 0, HealingPotionUsesLimit = ownCharacterId.HasValue ? ConsumableRules.HealingPotionUsesPerRun : 0, PendingSkillSlotMask = ownCharacterId.HasValue ? slot.PendingSkillSlotMask : 0, IsSoulImprintQueued = ownCharacterId.HasValue && slot.IsSoulImprintQueued, SoulImprint = ownSoulImprint, Consumables = ownConsumables, OperationPotion = operationPotion, Skills = ownSkills, CharacterName = character?.Name, CharacterElement = characterElement, OutgoingElementModifierPercent = ElementMatchup.PlayerAttackPercent(characterElement, monster.Element), IncomingElementModifierPercent = ElementMatchup.MonsterAttackPercent(monster.Element, characterElement), ProfessionName = character is null ? null : skillCatalog.EffectiveProfession(character)?.Name, CharacterHp = character?.Hp, CharacterMaxHp = character is null ? null : TalentRules.EffectiveMaxHp(character), CharacterLevel = character?.Level, CharacterExperience = character?.Experience, ExperienceToNextLevel = character is null ? null : progressionService.GetExperienceToNextLevel(character.Level), IsOccupied = slot.CharacterId.HasValue, IsCurrentUserCharacter = slot.UserId == currentUserId, IsQuickSkillCastEnabled = ownCharacterId.HasValue && character?.IsQuickSkillCastEnabled == true, IsAlive = character?.Hp > 0, IsConfirmed = slot.IsConfirmed, PlayerName = player?.UserName, IsAutoEnabled = isAuto, IsTemporaryAuto = slot.IsTemporaryAuto, IsOffline = isOffline, IsOfflineAuto = isOffline && isAuto, IsAutoUnlockedForCurrentUser = isAutoUnlocked, CanConfigureAuto = canConfigureAuto, StatusEffects = statusEffects };
            }).ToList()
        };
    }

    private sealed class CumulativeRewardEntry
    {
        public string RewardSource { get; init; } = "Base";
        public int CharacterId { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;
        public string? WeaponSnapshotJson { get; init; }
        public int Quantity { get; init; }
        public int PendingQuantity { get; init; }
    }

    private RoomCumulativeRewardsResponse BuildCumulativeRewards(List<CumulativeRewardEntry> entries,
        List<RewardRun> runs, IReadOnlyDictionary<int, string> names, List<int> currentCharacterIds)
    {
        var items = entries.Where(entry => entry.Kind is "Consumable" or "Material" or "Weapon" or "SoulImprint")
            .GroupBy(entry => new { entry.CharacterId, entry.Kind, entry.Code, entry.RewardSource,
                Name = DescribeRewardItem(new RewardEntry { Kind = entry.Kind, Code = entry.Code, WeaponSnapshotJson = entry.WeaponSnapshotJson }) })
            .Select(group => new RoomRewardItemResponse
            {
                CharacterId = group.Key.CharacterId,
                CharacterName = names.GetValueOrDefault(group.Key.CharacterId, "角色"),
                Kind = group.Key.Kind, Code = group.Key.Code, Name = group.Key.Name, Source = RewardSourceName(group.Key.RewardSource, "基础奖励"),
                Quantity = group.Sum(entry => entry.Quantity),
                PendingQuantity = group.Sum(entry => entry.PendingQuantity)
            }).OrderBy(item => item.CharacterId).ThenBy(item => item.Kind).ThenBy(item => item.Name).ToList();
        var entriesByCharacter = entries.ToLookup(entry => entry.CharacterId);
        var itemsByCharacter = items.ToLookup(item => item.CharacterId);
        var characters = currentCharacterIds.Concat(entriesByCharacter.Select(group => group.Key)).Distinct()
            .Select(characterId =>
            {
                var characterEntries = entriesByCharacter[characterId].ToList();
                return new RoomCharacterRewardsResponse
                {
                    CharacterId = characterId, CharacterName = names.GetValueOrDefault(characterId, "角色"),
                    Gold = characterEntries.Where(entry => entry.Kind == "Gold").Sum(entry => entry.Quantity),
                    Experience = characterEntries.Where(entry => entry.Kind == "Experience").Sum(entry => entry.Quantity),
                    PendingGold = characterEntries.Where(entry => entry.Kind == "Gold").Sum(entry => entry.PendingQuantity),
                    PendingExperience = characterEntries.Where(entry => entry.Kind == "Experience").Sum(entry => entry.PendingQuantity),
                    Items = itemsByCharacter[characterId].ToList()
                };
            }).ToList();
        return new RoomCumulativeRewardsResponse
        {
            CompletedRuns = runs.Count(run => run.Status != "Pending"),
            Gold = characters.Sum(character => character.Gold),
            MasteryGold = entries.Where(entry => entry.Kind == "Gold" && entry.RewardSource == "Mastery").Sum(entry => entry.Quantity),
            Experience = characters.Sum(character => character.Experience),
            HasPendingRewards = entries.Any(entry => entry.PendingQuantity > 0),
            Items = items, Characters = characters
        };
    }

    private string DescribeRewardItem(RewardEntry entry) => entry.Kind switch
    {
        "Consumable" => consumableCatalog.FindItem(entry.Code)?.Name ?? entry.Code,
        "Material" => materialCatalog?.FindItem(entry.Code)?.Name ?? entry.Code,
        "SoulImprint" => soulImprintCatalog?.Find(entry.Code)?.Name ?? entry.Code,
        _ => RewardCatalog.DeserializeWeapon(entry)?.DisplayName ?? entry.Code
    };

    private static string RewardSourceName(string source, string baseName) => source switch
    {
        "Mastery" => "精通追加", "Challenge" => "挑战奖励", _ => baseName
    };

    private async Task<RoomSummaryResponse?> BuildRoomSummaryAsync(Room room, int? currentUserId = null)
    {
        var dungeon = await dbContext.Dungeons.FindAsync(room.DungeonId);
        var monster = await dbContext.Monsters.FindAsync(room.MonsterId);
        if (monster is null) return null;
        var isCurrentUserParticipant = currentUserId.HasValue && await dbContext.RoomSlots
            .AnyAsync(slot => slot.RoomId == room.Id && slot.UserId == currentUserId.Value &&
                (slot.CharacterId.HasValue || room.ClosedAtUtc.HasValue));
        var enemiesInCurrentWave = monster.RoomId.HasValue
            ? await dbContext.Monsters.CountAsync(candidate => candidate.RoomId == room.Id && candidate.WaveNumber == monster.WaveNumber)
            : 1;
        return new RoomSummaryResponse
        {
            RoomId = room.Id, MonsterName = monster.Name, MonsterHp = monster.Hp, MonsterMaxHp = monster.MaxHp,
            RegionCode = dungeon?.RegionCode ?? "", RegionName = dungeon?.RegionName ?? "",
            DepthLevel = room.DepthLevel,
            DungeonName = dungeon is not null && (_depthCatalog.Find(dungeon.Code) is not null || room.DepthDefinitionJson is not null)
                ? _depthCatalog.DisplayName(dungeon.Name, room.DepthLevel) : dungeon?.Name ?? "",
            CurrentWaveNumber = room.CurrentWaveNumber, TotalWaveCount = room.TotalWaveCount,
            CurrentEnemyNumber = monster.Position, EnemiesInCurrentWave = enemiesInCurrentWave,
            RoomStatus = room.Status, IsRepeatBattle = room.IsRepeatBattle, IsPublic = room.IsPublic,
            ExpiresAtUtc = room.ExpiresAtUtc, ClosedAtUtc = room.ClosedAtUtc,
            IsPreparationTimeoutEnabled = room.IsPreparationTimeoutEnabled,
            IsCurrentUserParticipant = isCurrentUserParticipant,
            PendingOperationCount = currentUserId.HasValue ? await dbContext.RoomOperations.CountAsync(operation =>
                operation.RoomId == room.Id && operation.UserId == currentUserId.Value && operation.Status == "Pending") : 0,
            IsOwnedByCurrentUser = currentUserId.HasValue && room.OwnerUserId == currentUserId.Value
        };
    }

    private List<DungeonRewardPreviewResponse> BuildRewardPreview(Dungeon dungeon)
    {
        var result = new List<DungeonRewardPreviewResponse>();
        var sources = encounterCatalog?.GetRewardSources(dungeon) ?? [new EncounterRewardSource(dungeon.Code, false)];
        foreach (var source in sources)
        {
            var sourceName = source.IsBoss ? "首领掉落" : "怪物掉落";
            result.AddRange(rewardService.GetDropPreview(source.Code, false).Select(drop => BuildDropPreview(sourceName, drop)));
        }
        result.AddRange(rewardService.GetDropPreview(dungeon.Code, true).Select(drop => BuildDropPreview("通关奖励", drop)));
        result.AddRange(rewardService.GetDropPreview($"{dungeon.Code}-first-clear", true).Select(drop => BuildDropPreview("首次通关", drop)));
        return result.DistinctBy(item => (item.Source, item.Kind, item.Code, item.Quantity, item.ChancePercent)).ToList();
    }

    private MonsterPreviewResponse BuildMonsterPreview(Dungeon dungeon, Monster monster)
    {
        var rewardCode = string.IsNullOrWhiteSpace(monster.RewardProfileCode) ? dungeon.Code : monster.RewardProfileCode;
        var source = monster.IsBoss ? "首领掉落" : "怪物掉落";
        return new MonsterPreviewResponse
        {
            Name = monster.Name, Element = monster.Element, MaxHp = monster.MaxHp,
            Attack = monster.Attack, Defense = monster.Defense, WaveNumber = monster.WaveNumber,
            Position = monster.Position, IsBoss = monster.IsBoss,
            Drops = rewardService.GetDropPreview(rewardCode, false)
                .Select(drop => BuildDropPreview(source, drop)).ToList()
        };
    }

    private static DungeonRewardPreviewResponse BuildDropPreview(string source, RewardDropPreview drop) => new()
    {
        Source = source, Kind = drop.Kind, Code = drop.Code, Name = drop.Name,
        Quantity = drop.Quantity, ChancePercent = drop.ChancePercent,
        Weapon = drop.Weapon is null ? null : new WeaponDropPreviewResponse
        {
            Element = drop.Weapon.Element, ItemLevel = drop.Weapon.ItemLevel,
            Attack = drop.Weapon.Attack, MaxHp = drop.Weapon.MaxHp,
            Skills = drop.Weapon.Skills.Select(skill => new WeaponDropSkillPreviewResponse
            {
                Name = skill.Name, Level = skill.Level, Description = skill.Description,
                UnlockQualityRank = skill.UnlockQualityRank
            }).ToList()
        }
    };

}
