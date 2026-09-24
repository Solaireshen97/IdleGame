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

namespace Game.Server.Services;

public class RoomService(GameDbContext dbContext, UserService userService, ProgressionService progressionService, ConsumableCatalog consumableCatalog, SkillCatalog skillCatalog, RewardService rewardService, DungeonEncounterCatalog? encounterCatalog = null, MonsterCombatService? monsterCombatService = null, BattleLogStore? battleLogStore = null, WorldCatalog? worldCatalog = null, IOptions<ActivityOptions>? activityOptions = null, MaterialCatalog? materialCatalog = null, WeaponCatalog? weaponCatalog = null)
{
    private const int SlotCount = 5;
    private TimeSpan MaximumActivityDuration => TimeSpan.FromHours(activityOptions?.Value.MaximumHours is > 0 and <= 168 ? activityOptions.Value.MaximumHours : 12);

    public async Task<List<RoomSummaryResponse>> GetRoomsAsync(string? token = null)
    {
        var (currentUser, userError) = await userService.GetCurrentUserEntityAsync(token);
        var currentUserId = userError is null ? currentUser!.Id : (int?)null;
        var rooms = await dbContext.Rooms.Where(room => room.ClosedAtUtc == null ||
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
        if (room.ClosedAtUtc.HasValue && (error is not null ||
            room.OwnerUserId != user!.Id && !await dbContext.RoomSlots.AnyAsync(slot => slot.RoomId == roomId && slot.UserId == user.Id)))
            return null;
        return await BuildRoomDetailAsync(room, error is null ? user!.Id : null);
    }

    public async Task<List<DungeonSummaryResponse>> GetDungeonsAsync(string? token)
    {
        var (_, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        var clearedDungeonCodes = error is null
            ? await dbContext.CharacterBattleMilestones.Where(milestone =>
                    milestone.CharacterId == character!.Id &&
                    milestone.Kind == BattleMilestoneService.DungeonClearKind && milestone.Count > 0)
                .Select(milestone => milestone.TargetCode).ToListAsync()
            : [];
        var currentLevel = error is null ? character!.Level : 0;
        var dungeons = await dbContext.Dungeons.Where(dungeon => dungeon.IsVisible)
            .OrderBy(dungeon => dungeon.SortOrder).ToListAsync();
        return dungeons.Select(dungeon =>
        {
            var canEnter = currentLevel >= dungeon.MinimumLevel;
            return new DungeonSummaryResponse
            {
                DungeonId = dungeon.Id, Code = dungeon.Code, Name = dungeon.Name,
                RegionCode = dungeon.RegionCode, RegionName = dungeon.RegionName, DungeonKind = dungeon.DungeonKind,
                Description = dungeon.Description, MinimumLevel = dungeon.MinimumLevel,
                RecommendedLevel = dungeon.RecommendedLevel, CurrentCharacterLevel = currentLevel,
                ExperiencePercent = progressionService.ApplyDungeonExperienceModifier(100, currentLevel, dungeon.MinimumLevel),
                CanEnter = canEnter,
                LockReason = canEnter ? null : $"需要角色达到 Lv.{dungeon.MinimumLevel}",
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
            };
        }).ToList();
    }

    public async Task<DungeonSummaryResponse?> GetDungeonAsync(int dungeonId, string? token) =>
        (await GetDungeonsAsync(token)).SingleOrDefault(dungeon => dungeon.DungeonId == dungeonId);

    public Task<(RoomDetailResponse? Detail, string? Error)> CreateRoomAsync(string monsterType, string? token) =>
        CreateRoomAsync(null, monsterType, token);

    public async Task<(RoomDetailResponse? Detail, string? Error)> CreateRoomAsync(int? dungeonId, string? legacyMonsterType, string? token, bool isRepeatBattle = false, bool isPreparationTimeoutEnabled = true)
    {
        var (user, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        if (await CharacterActivityManager.IsBusyAsync(dbContext, character!.Id)) return (null, "CharacterAlreadyInRoom");

        await DbInitializer.EnsureDefaultDungeonsAsync(dbContext, worldCatalog);
        var dungeon = dungeonId.HasValue
            ? await dbContext.Dungeons.FindAsync(dungeonId.Value)
            : await dbContext.Dungeons.FirstOrDefaultAsync(item => item.MonsterName == legacyMonsterType) ?? await dbContext.Dungeons.OrderBy(item => item.SortOrder).FirstAsync();
        if (dungeon is null || dungeonId.HasValue && !dungeon.IsVisible) return (null, "DungeonNotFound");
        if (character!.Level < dungeon.MinimumLevel) return (null, "CharacterLevelTooLow");
        character.Hp = TalentRules.EffectiveMaxHp(character);
        var monsters = (encounterCatalog?.CreateMonsters(dungeon) ??
            [new Monster { Name = dungeon.MonsterName, Element = dungeon.MonsterElement, Hp = dungeon.MonsterMaxHp, MaxHp = dungeon.MonsterMaxHp, Attack = dungeon.MonsterAttack, Defense = dungeon.MonsterDefense }]).ToList();
        var firstMonster = monsters.OrderBy(monster => monster.WaveNumber).ThenBy(monster => monster.Position).First();
        var now = DateTime.UtcNow;
        var room = new Room
        {
            DungeonId = dungeon.Id, MonsterId = 0, OwnerUserId = user!.Id, SlotCount = dungeon.SlotCount,
            Status = RoomStatus.NotStarted, IsRepeatBattle = isRepeatBattle,
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
            LastSeenAtUtc = index == 1 ? now : null,
            IsMainControl = index == 1
        }));
        CharacterActivityManager.StartBattle(dbContext, character.Id, room, now);
        try { await dbContext.SaveChangesAsync(); }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            await transaction.RollbackAsync();
            return (null, "CharacterAlreadyInRoom");
        }
        if (monsterCombatService is not null) await monsterCombatService.EnsureIntentAsync(room, firstMonster);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return (await BuildRoomDetailAsync(room, user.Id), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> JoinRoomAsync(int roomId, JoinRoomRequest request, string? token)
    {
        var room = await dbContext.Rooms.FindAsync(roomId);
        if (room is null) return (null, "NotFound");
        var (user, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        if (room.ClosedAtUtc.HasValue) return (null, "RoomClosed");
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
        var minimumLevel = await dbContext.Dungeons.Where(dungeon => dungeon.Id == room.DungeonId)
            .Select(dungeon => (int?)dungeon.MinimumLevel).SingleOrDefaultAsync();
        if (!minimumLevel.HasValue) return (null, "DungeonNotFound");
        if (character!.Level < minimumLevel.Value) return (null, "CharacterLevelTooLow");
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

    public async Task<(RoomDetailResponse? Detail, string? Error)> LeaveRoomAsync(int roomId, string? token)
    {
        var room = await dbContext.Rooms.FindAsync(roomId);
        if (room is null) return (null, "NotFound");
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
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
        slot.PendingConsumableSlotIndex = null;
        slot.PendingSkillSlotMask = 0;
        slot.HasParticipatedInRun = false;
        slot.LastParticipatedMonsterId = null;
        room.Version++;
        await dbContext.SaveChangesAsync();
        return (await BuildRoomDetailAsync(room, user.Id), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> AssignSlotAsync(int roomId, AssignRoomSlotRequest request, string? token)
    {
        var (room, user, error) = await GetOwnerEditableRoomAsync(roomId, token);
        if (error is not null) return (null, error);
        if (request.SlotIndex < 1 || request.SlotIndex > room!.SlotCount) return (null, "InvalidSlotIndex");
        var character = await dbContext.Characters.FirstOrDefaultAsync(x => x.Id == request.CharacterId);
        if (character is null) return (null, "CharacterNotFound");
        if (character.UserId != user!.Id) return (null, "NotCharacterOwner");
        var minimumLevel = await dbContext.Dungeons.Where(dungeon => dungeon.Id == room!.DungeonId)
            .Select(dungeon => (int?)dungeon.MinimumLevel).SingleOrDefaultAsync();
        if (!minimumLevel.HasValue) return (null, "DungeonNotFound");
        if (character.Level < minimumLevel.Value) return (null, "CharacterLevelTooLow");
        var existingSlot = await dbContext.RoomSlots.FirstOrDefaultAsync(x => x.CharacterId == character.Id);
        if (existingSlot is null && await dbContext.CharacterActivities.AnyAsync(activity => activity.CharacterId == character.Id))
            return (null, "CharacterAlreadyInRoom");
        if (existingSlot is not null && existingSlot.RoomId != room!.Id) return (null, "CharacterAlreadyInRoom");
        if (existingSlot is not null && existingSlot.SlotIndex != request.SlotIndex) return (null, "CharacterAlreadyInTargetRoom");
        var target = await dbContext.RoomSlots.SingleAsync(x => x.RoomId == room!.Id && x.SlotIndex == request.SlotIndex);
        if (target.CharacterId.HasValue && target.CharacterId != character.Id) return (null, "SlotOccupied");
        if (target.IsMainControl && target.CharacterId != character.Id) return (null, "CannotReplaceMainControl");
        character.Hp = TalentRules.EffectiveMaxHp(character);
        target.CharacterId = character.Id;
        target.UserId = user.Id;
        target.LastSeenAtUtc = DateTime.UtcNow;
        if (existingSlot is null)
        {
            target.HasParticipatedInRun = false;
            target.LastParticipatedMonsterId = null;
        }
        if (existingSlot is null) CharacterActivityManager.StartBattle(dbContext, character.Id, room, DateTime.UtcNow);
        target.PendingConsumableSlotIndex = null;
        target.PendingSkillSlotMask = 0;
        room!.Version++;
        try { await dbContext.SaveChangesAsync(); }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            return (null, "CharacterAlreadyInRoom");
        }
        return (await BuildRoomDetailAsync(room, user.Id), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> RemoveSlotAsync(int roomId, int slotIndex, string? token)
    {
        var (room, user, error) = await GetOwnerEditableRoomAsync(roomId, token);
        if (error is not null) return (null, error);
        if (slotIndex < 1 || slotIndex > room!.SlotCount) return (null, "InvalidSlotIndex");
        var slot = await dbContext.RoomSlots.SingleAsync(x => x.RoomId == room!.Id && x.SlotIndex == slotIndex);
        if (slot.IsMainControl) return (null, "CannotRemoveMainControl");
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
        slot.PendingConsumableSlotIndex = null;
        slot.PendingSkillSlotMask = 0;
        slot.HasParticipatedInRun = false;
        slot.LastParticipatedMonsterId = null;
        room.Version++;
        await dbContext.SaveChangesAsync();
        return (await BuildRoomDetailAsync(room, user!.Id), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> SetMainControlAsync(int roomId, SetMainControlRequest request, string? token)
    {
        var (room, user, error) = await GetOwnerEditableRoomAsync(roomId, token);
        if (error is not null) return (null, error);
        var slot = await dbContext.RoomSlots.SingleOrDefaultAsync(x => x.RoomId == room!.Id && x.CharacterId == request.CharacterId && x.UserId == user!.Id);
        if (slot is null) return (null, "CharacterNotInRoom");
        var slots = await dbContext.RoomSlots.Where(x => x.RoomId == room.Id).ToListAsync();
        foreach (var item in slots) item.IsMainControl = item.Id == slot.Id;
        await dbContext.SaveChangesAsync();
        return (await BuildRoomDetailAsync(room, user.Id), null);
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

    private async Task<(Room? Room, User? User, string? Error)> GetOwnerEditableRoomAsync(int roomId, string? token)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null) return (null, null, "NotFound");
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (room, null, error);
        if (room.OwnerUserId != user!.Id) return (room, user, "NotOwner");
        if (room.ClosedAtUtc.HasValue) return (room, null, "RoomClosed");
        if (room.IsRepeatBattle && room.ExpiresAtUtc <= DateTime.UtcNow &&
            (room.Status == RoomStatus.BattleOver || room.Status == RoomStatus.NotStarted && room.RoundNumber == 0))
        {
            await CharacterActivityManager.CloseBattleRoomAsync(dbContext, room, DateTime.UtcNow);
            room.Version++;
            await dbContext.SaveChangesAsync();
            return (room, null, "RoomClosed");
        }
        if (room.Status != RoomStatus.BattleOver && (room.Status != RoomStatus.NotStarted || room.RoundNumber > 0)) return (room, user, "FormationLocked");
        return (room, user, null);
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
        var skillSlots = await dbContext.CharacterSkillSlots
            .Where(entry => currentUserCharacterIds.Contains(entry.CharacterId)).ToListAsync();
        var skillCooldowns = await dbContext.BattleSkillCooldowns
            .Where(entry => entry.RoomId == room.Id && currentUserCharacterIds.Contains(entry.CharacterId)).ToListAsync();
        var purchasedSkillNodes = (await dbContext.CharacterSkillTalents
            .Where(entry => currentUserCharacterIds.Contains(entry.CharacterId)).ToListAsync())
            .GroupBy(entry => entry.CharacterId)
            .ToDictionary(group => group.Key,
                group => group.ToDictionary(entry => entry.NodeCode, entry => entry.PointsSpent, StringComparer.OrdinalIgnoreCase));
        var isCurrentUserAutoUnlocked = currentUserCharacterIds.Any(clearedDungeonCharacterIds.Contains);
        var isMixedTeam = slots.Any(slot => slot.UserId.HasValue && slot.UserId != room.OwnerUserId);
        var isPreparationTimeoutEnabled = room.IsPreparationTimeoutEnabled;
        var isAllAliveMembersAuto = aliveSlots.Count > 0 && aliveSlots.All(slot =>
            RoomAutoPolicy.IsAuto(room, slot, clearedDungeonCharacterIds));
        var rewardRun = currentUserId.HasValue
            ? await dbContext.RewardRuns.Where(run => run.RoomId == room.Id && run.Sequence <= room.RunSequence)
                .OrderByDescending(run => run.Sequence).FirstOrDefaultAsync()
            : null;
        var rewardEntries = rewardRun is null ? [] : await dbContext.RewardEntries
            .Where(entry => entry.RoomId == room.Id && entry.Sequence == rewardRun.Sequence && entry.UserId == currentUserId)
            .OrderBy(entry => entry.Id).ToListAsync();
        var rewardCharacterIds = rewardEntries.Select(entry => entry.CharacterId).Distinct().ToList();
        var rewardCharacters = await dbContext.Characters.Where(character => rewardCharacterIds.Contains(character.Id))
            .ToDictionaryAsync(character => character.Id, character => character.Name);
        var cumulativeEntries = room.ClosedAtUtc.HasValue && currentUserId.HasValue
            ? await dbContext.RewardEntries.Where(entry => entry.RoomId == room.Id && entry.UserId == currentUserId.Value).ToListAsync()
            : [];
        var completedRunCount = room.ClosedAtUtc.HasValue && currentUserId.HasValue
            ? await dbContext.RewardRuns.CountAsync(run => run.RoomId == room.Id && run.Status != "Pending")
            : 0;
        var cumulativeCharacterIds = cumulativeEntries.Select(entry => entry.CharacterId).Distinct().ToList();
        var cumulativeCharacters = await dbContext.Characters.Where(character => cumulativeCharacterIds.Contains(character.Id))
            .ToDictionaryAsync(character => character.Id, character => character.Name);
        var cumulativeItems = cumulativeEntries.Where(entry => entry.Kind is "Consumable" or "Material" or "Weapon")
            .Select(entry => new RoomRewardItemResponse
            {
                CharacterName = cumulativeCharacters.GetValueOrDefault(entry.CharacterId, "角色"),
                Kind = entry.Kind, Quantity = entry.Quantity, Source = "累计",
                Name = entry.Kind switch
                {
                    "Consumable" => consumableCatalog.FindItem(entry.Code)?.Name ?? entry.Code,
                    "Material" => materialCatalog?.FindItem(entry.Code)?.Name ?? entry.Code,
                    _ => RewardCatalog.DeserializeWeapon(entry)?.DisplayName ?? entry.Code
                }
            })
            .GroupBy(item => new { item.CharacterName, item.Kind, item.Name })
            .Select(group => new RoomRewardItemResponse
            {
                CharacterName = group.Key.CharacterName, Kind = group.Key.Kind, Name = group.Key.Name,
                Source = "累计", Quantity = group.Sum(item => item.Quantity)
            }).OrderBy(item => item.CharacterName).ThenBy(item => item.Name).ToList();
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
        return new RoomDetailResponse
        {
            RoomId = room.Id, OwnerUserId = room.OwnerUserId, DungeonId = dungeon.Id, DungeonName = dungeon.Name, SlotCount = room.SlotCount,
            RegionName = dungeon.RegionName,
            MonsterName = monster.Name, MonsterElement = monster.Element, MonsterHp = monster.Hp, MonsterMaxHp = monster.MaxHp,
            CurrentWaveNumber = room.CurrentWaveNumber, TotalWaveCount = room.TotalWaveCount,
            CurrentEnemyNumber = monster.Position, EnemiesInCurrentWave = enemiesInCurrentWave,
            RoomStatus = room.Status, RunSequence = room.RunSequence, RoundNumber = room.RoundNumber, NextRoundAvailableAtUtc = room.NextRoundAvailableAtUtc, RoundCooldownDurationSeconds = room.RoundCooldownDurationSeconds, PreparationStartedAtUtc = room.PreparationStartedAtUtc, PreparationExpiresAtUtc = isPreparationTimeoutEnabled ? room.PreparationStartedAtUtc?.AddSeconds(BattleRules.PreparationTimeoutSeconds) : null, BattleEndedAtUtc = room.BattleEndedAtUtc,
            IsRepeatBattle = room.IsRepeatBattle, StartedAtUtc = room.StartedAtUtc,
            ExpiresAtUtc = room.ExpiresAtUtc, ClosedAtUtc = room.ClosedAtUtc,
            NextBattleStartAtUtc = room.ClosedAtUtc is null && room.IsRepeatBattle && room.Status == RoomStatus.BattleOver && monster.Hp <= 0 ? room.BattleEndedAtUtc?.AddSeconds(BattleRules.RepeatBattleDelaySeconds) : null,
            NextWaveStartAtUtc = room.Status == RoomStatus.WaveTransition ? room.NextRoundAvailableAtUtc : null,
            ServerTimeUtc = now,
            CanExecuteRound = room.Status == RoomStatus.Preparing && aliveSlots.Count > 0 && aliveSlots.All(x => x.IsConfirmed) && monster.Hp > 0,
            IsMixedTeam = isMixedTeam, IsPreparationTimeoutEnabled = isPreparationTimeoutEnabled, PreparationTimeoutSeconds = BattleRules.PreparationTimeoutSeconds, IsCurrentUserAutoUnlocked = isCurrentUserAutoUnlocked, IsAllAliveMembersAuto = isAllAliveMembersAuto,
            CanPrepare = room.ClosedAtUtc is null && currentUserAliveSlots.Any(x => !x.IsConfirmed) && !isAllAliveMembersAuto && monster.Hp > 0 &&
                room.Status is RoomStatus.NotStarted or RoomStatus.Preparing or RoomStatus.Cooldown,
            CanLeaveRoom = room.ClosedAtUtc is null && currentUserId.HasValue && currentUserId != room.OwnerUserId &&
                (room.Status == RoomStatus.BattleOver || room.Status == RoomStatus.NotStarted && room.RoundNumber == 0) &&
                slots.Any(x => x.UserId == currentUserId),
            MonsterIntent = monsterIntent,
            MonsterEffects = monsterEffects,
            BattleLogs = battleLogStore?.Get(room.Id) ?? [],
            CumulativeRewards = room.ClosedAtUtc.HasValue ? new RoomCumulativeRewardsResponse
            {
                CompletedRuns = completedRunCount,
                Gold = cumulativeEntries.Where(entry => entry.Kind == "Gold").Sum(entry => entry.Quantity),
                Experience = cumulativeEntries.Where(entry => entry.Kind == "Experience").Sum(entry => entry.Quantity),
                Items = cumulativeItems
            } : null,
            Rewards = rewardRun is null || rewardRun.Sequence < room.RunSequence && rewardEntries.Count == 0
                ? null : new RoomRewardSummaryResponse
            {
                RunSequence = rewardRun.Sequence, IsCurrentRun = rewardRun.Sequence == room.RunSequence,
                Status = rewardRun.Status, SettledAtUtc = rewardRun.SettledAtUtc,
                Gold = rewardEntries.Where(entry => entry.Kind == "Gold").Sum(entry => entry.Quantity),
                Experience = rewardEntries.Where(entry => entry.Kind == "Experience").Sum(entry => entry.Quantity),
                Items = rewardEntries.Where(entry => entry.Kind is "Consumable" or "Weapon")
                    .Select(entry => new RoomRewardItemResponse
                    {
                        CharacterName = rewardCharacters.GetValueOrDefault(entry.CharacterId, "角色"),
                        Kind = entry.Kind, Quantity = entry.Quantity,
                        Source = entry.EventKey == "clear" ? "通关" : "击杀",
                        Name = entry.Kind == "Consumable" ? consumableCatalog.FindItem(entry.Code)?.Name ?? entry.Code
                            : RewardCatalog.DeserializeWeapon(entry)?.DisplayName ?? entry.Code
                    }).ToList()
            },
            Slots = slots.Select(slot =>
            {
                characters.TryGetValue(slot.CharacterId ?? 0, out var character);
                users.TryGetValue(slot.UserId ?? 0, out var player);
                var ownCharacterId = slot.UserId == currentUserId ? slot.CharacterId : null;
                var ownedSkillNodes = ownCharacterId is int ownedId && purchasedSkillNodes.TryGetValue(ownedId, out var nodes)
                    ? nodes : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
                            AutoHpThresholdPercent = equipped?.AutoHpThresholdPercent ?? ConsumableRules.DefaultAutoHpThresholdPercent
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
                        var skill = skillCatalog.FindSkill(equipped?.SkillCode);
                        if (skill is not null && (character is null || !skillCatalog.IsLearned(character, skill.Code, ownedSkillNodes)))
                            skill = null;
                        var cooldown = skill is null ? null : skillCooldowns.FirstOrDefault(entry => entry.CharacterId == skillCharacterId && entry.SkillCode == skill.Code);
                        return new RoomSkillSlotResponse
                        {
                            SlotIndex = index,
                            SkillCode = skill?.Code,
                            SkillName = skill?.Name,
                            Description = skill?.Description,
                            EffectType = skill is null ? null : SkillCatalog.PrimaryEffectType(skill),
                            Power = skill is null ? 0 : SkillCatalog.PrimaryPower(skill),
                            AutoCondition = skill is null ? "Always" : SkillCatalog.AutoConditionFor(skill),
                            Effects = skill is null ? [] : SkillCatalog.EffectsFor(skill).Select(effect => new SkillEffectResponse
                            {
                                Type = effect.Type, Target = effect.Target, Power = effect.Power,
                                StatusCode = effect.StatusCode, DurationRounds = effect.DurationRounds
                            }).ToList(),
                            CooldownRoundsRemaining = Math.Max(0, (cooldown?.ReadyAtRound ?? 0) - room.RoundNumber),
                            AutoUseEnabled = skill is not null && equipped?.AutoUseEnabled == true,
                            AutoHpThresholdPercent = equipped?.AutoHpThresholdPercent ?? SkillRules.DefaultAutoHpThresholdPercent
                        };
                    }).ToList()
                    : [];
                var characterElement = slot.CharacterId is int characterId && mainWeaponElements.TryGetValue(characterId, out var element)
                    ? element : (ElementType?)null;
                var canConfigureAuto = slot.UserId == currentUserId && slot.CharacterId.HasValue &&
                    clearedDungeonCharacterIds.Contains(slot.CharacterId.Value) && character?.Hp > 0 &&
                    (slot.UserId != room.OwnerUserId || slot.IsMainControl);
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
                var isAuto = RoomAutoPolicy.IsAuto(room, slot, clearedDungeonCharacterIds);
                var isOffline = RoomAutoPolicy.IsOffline(room, slot, now);
                return new RoomSlotResponse { SlotIndex = slot.SlotIndex, CharacterId = slot.CharacterId, PendingConsumableSlotIndex = ownCharacterId.HasValue ? slot.PendingConsumableSlotIndex : null, PendingSkillSlotMask = ownCharacterId.HasValue ? slot.PendingSkillSlotMask : 0, Consumables = ownConsumables, OperationPotion = operationPotion, Skills = ownSkills, CharacterName = character?.Name, CharacterElement = characterElement, OutgoingElementModifierPercent = ElementMatchup.PlayerAttackPercent(characterElement, monster.Element), IncomingElementModifierPercent = ElementMatchup.MonsterAttackPercent(monster.Element, characterElement), ProfessionName = character is null ? null : skillCatalog.EffectiveProfession(character)?.Name, CharacterHp = character?.Hp, CharacterMaxHp = character is null ? null : TalentRules.EffectiveMaxHp(character), CharacterLevel = character?.Level, CharacterExperience = character?.Experience, ExperienceToNextLevel = character is null ? null : progressionService.GetExperienceToNextLevel(character.Level), TalentPoints = character?.TalentPoints, IsOccupied = slot.CharacterId.HasValue, IsMainControl = slot.IsMainControl, IsCurrentUserCharacter = slot.UserId == currentUserId, IsAlive = character?.Hp > 0, IsConfirmed = slot.IsConfirmed, PlayerName = player?.UserName, IsAutoEnabled = isAuto, IsTemporaryAuto = slot.IsTemporaryAuto, IsOffline = isOffline, IsOfflineAuto = isOffline && isAuto, IsAutoUnlockedForCurrentUser = canConfigureAuto, CanConfigureAuto = canConfigureAuto, StatusEffects = statusEffects };
            }).ToList()
        };
    }

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
            RegionCode = dungeon?.RegionCode ?? "", RegionName = dungeon?.RegionName ?? "", DungeonName = dungeon?.Name ?? "",
            CurrentWaveNumber = room.CurrentWaveNumber, TotalWaveCount = room.TotalWaveCount,
            CurrentEnemyNumber = monster.Position, EnemiesInCurrentWave = enemiesInCurrentWave,
            RoomStatus = room.Status, IsRepeatBattle = room.IsRepeatBattle,
            ExpiresAtUtc = room.ExpiresAtUtc, ClosedAtUtc = room.ClosedAtUtc,
            IsPreparationTimeoutEnabled = room.IsPreparationTimeoutEnabled,
            IsCurrentUserParticipant = isCurrentUserParticipant,
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
                Name = skill.Name, Level = skill.Level, Description = skill.Description
            }).ToList()
        }
    };

}
