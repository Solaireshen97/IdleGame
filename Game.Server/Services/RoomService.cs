using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class RoomService(GameDbContext dbContext, UserService userService, ProgressionService progressionService, ConsumableCatalog consumableCatalog, SkillCatalog skillCatalog, RewardService rewardService, DungeonEncounterCatalog? encounterCatalog = null, MonsterCombatService? monsterCombatService = null, BattleLogStore? battleLogStore = null, WorldCatalog? worldCatalog = null)
{
    private const int SlotCount = 5;

    public async Task<List<RoomSummaryResponse>> GetRoomsAsync()
    {
        var rooms = await dbContext.Rooms.ToListAsync();
        var result = new List<RoomSummaryResponse>();
        foreach (var room in rooms)
        {
            var summary = await BuildRoomSummaryAsync(room);
            if (summary is not null) result.Add(summary);
        }
        return result;
    }

    public async Task<RoomDetailResponse?> GetRoomDetailAsync(int roomId, string? token = null)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null) return null;
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        return await BuildRoomDetailAsync(room, error is null ? user!.Id : null);
    }

    public async Task<List<DungeonSummaryResponse>> GetDungeonsAsync(string? token)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        var clearedDungeonIds = error is null
            ? await dbContext.UserDungeonClears.Where(clear => clear.UserId == user!.Id).Select(clear => clear.DungeonId).ToListAsync()
            : [];
        var currentLevel = error is null && user!.ActiveCharacterId.HasValue
            ? await dbContext.Characters.Where(character => character.Id == user.ActiveCharacterId.Value && character.UserId == user.Id)
                .Select(character => character.Level).SingleOrDefaultAsync()
            : 0;
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
                CanEnter = canEnter,
                LockReason = canEnter ? null : $"需要角色达到 Lv.{dungeon.MinimumLevel}",
                MonsterName = dungeon.MonsterName, MonsterElement = dungeon.MonsterElement,
                MonsterMaxHp = dungeon.MonsterMaxHp, MonsterAttack = dungeon.MonsterAttack,
                MonsterDefense = dungeon.MonsterDefense, SlotCount = dungeon.SlotCount,
                WaveCount = encounterCatalog?.GetWaveCount(dungeon) ?? 1,
                MonsterCount = encounterCatalog?.GetMonsterCount(dungeon) ?? 1,
                IsClearedByCurrentUser = clearedDungeonIds.Contains(dungeon.Id),
                AutoUnlocked = clearedDungeonIds.Contains(dungeon.Id),
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
        if (await dbContext.RoomSlots.AnyAsync(x => x.CharacterId == character!.Id)) return (null, "CharacterAlreadyInRoom");

        await DbInitializer.EnsureDefaultDungeonsAsync(dbContext, worldCatalog);
        var dungeon = dungeonId.HasValue
            ? await dbContext.Dungeons.FindAsync(dungeonId.Value)
            : await dbContext.Dungeons.FirstOrDefaultAsync(item => item.MonsterName == legacyMonsterType) ?? await dbContext.Dungeons.OrderBy(item => item.SortOrder).FirstAsync();
        if (dungeon is null || dungeonId.HasValue && !dungeon.IsVisible) return (null, "DungeonNotFound");
        if (character!.Level < dungeon.MinimumLevel) return (null, "CharacterLevelTooLow");
        var monsters = (encounterCatalog?.CreateMonsters(dungeon) ??
            [new Monster { Name = dungeon.MonsterName, Element = dungeon.MonsterElement, Hp = dungeon.MonsterMaxHp, MaxHp = dungeon.MonsterMaxHp, Attack = dungeon.MonsterAttack, Defense = dungeon.MonsterDefense }]).ToList();
        var firstMonster = monsters.OrderBy(monster => monster.WaveNumber).ThenBy(monster => monster.Position).First();
        var room = new Room
        {
            DungeonId = dungeon.Id, MonsterId = 0, OwnerUserId = user!.Id, SlotCount = dungeon.SlotCount,
            Status = RoomStatus.NotStarted, IsRepeatBattle = isRepeatBattle,
            IsPreparationTimeoutEnabled = isPreparationTimeoutEnabled,
            PreparationStartedAtUtc = isPreparationTimeoutEnabled ? DateTime.UtcNow : null,
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
            IsMainControl = index == 1
        }));
        await dbContext.SaveChangesAsync();
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
        var minimumLevel = await dbContext.Dungeons.Where(dungeon => dungeon.Id == room.DungeonId)
            .Select(dungeon => (int?)dungeon.MinimumLevel).SingleOrDefaultAsync();
        if (!minimumLevel.HasValue) return (null, "DungeonNotFound");
        if (character!.Level < minimumLevel.Value) return (null, "CharacterLevelTooLow");
        if (room.OwnerUserId == user!.Id) return (null, "CannotJoinOwnRoom");
        if (room.Status == RoomStatus.BattleOver) return (null, "BattleOver");
        if (room.Status != RoomStatus.NotStarted || room.RoundNumber > 0) return (null, "RoomLocked");
        if (request.SlotIndex < 1 || request.SlotIndex > room.SlotCount) return (null, "InvalidSlotIndex");
        if (await dbContext.RoomSlots.AnyAsync(x => x.RoomId == roomId && x.UserId == user.Id)) return (null, "AlreadyInRoom");
        if (await dbContext.RoomSlots.AnyAsync(x => x.CharacterId == character!.Id)) return (null, "CharacterAlreadyInRoom");
        var slot = await dbContext.RoomSlots.SingleAsync(x => x.RoomId == roomId && x.SlotIndex == request.SlotIndex);
        if (slot.CharacterId.HasValue) return (null, "SlotOccupied");
        slot.CharacterId = character.Id;
        slot.UserId = user.Id;
        room.Version++;
        await dbContext.SaveChangesAsync();
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
        slot.CharacterId = null;
        slot.UserId = null;
        slot.IsConfirmed = false;
        slot.IsAutoEnabled = false;
        slot.IsTemporaryAuto = false;
        slot.PendingConsumableSlotIndex = null;
        slot.PendingSkillSlotMask = 0;
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
        if (existingSlot is not null && existingSlot.RoomId != room!.Id) return (null, "CharacterAlreadyInRoom");
        if (existingSlot is not null && existingSlot.SlotIndex != request.SlotIndex) return (null, "CharacterAlreadyInTargetRoom");
        var target = await dbContext.RoomSlots.SingleAsync(x => x.RoomId == room!.Id && x.SlotIndex == request.SlotIndex);
        if (target.CharacterId.HasValue && target.CharacterId != character.Id) return (null, "SlotOccupied");
        if (target.IsMainControl && target.CharacterId != character.Id) return (null, "CannotReplaceMainControl");
        target.CharacterId = character.Id;
        target.UserId = user.Id;
        target.PendingConsumableSlotIndex = null;
        target.PendingSkillSlotMask = 0;
        room!.Version++;
        await dbContext.SaveChangesAsync();
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
        slot.CharacterId = null;
        slot.UserId = null;
        slot.IsConfirmed = false;
        slot.IsAutoEnabled = false;
        slot.IsTemporaryAuto = false;
        slot.PendingConsumableSlotIndex = null;
        slot.PendingSkillSlotMask = 0;
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
        dbContext.RoomSlots.RemoveRange(await dbContext.RoomSlots.Where(x => x.RoomId == roomId).ToListAsync());
        dbContext.BattleConsumableCooldowns.RemoveRange(await dbContext.BattleConsumableCooldowns.Where(x => x.RoomId == roomId).ToListAsync());
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
        var mainWeaponElements = await dbContext.CharacterWeapons
            .Where(weapon => characterIds.Contains(weapon.CharacterId) && weapon.EquippedSlotIndex == WeaponRules.MainSlotIndex)
            .ToDictionaryAsync(weapon => weapon.CharacterId, weapon => weapon.Element);
        var userIds = slots.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).ToList();
        var users = await dbContext.Users.Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var now = DateTime.UtcNow;
        var aliveSlots = slots.Where(x => x.CharacterId.HasValue && characters.TryGetValue(x.CharacterId.Value, out var character) && character.Hp > 0).ToList();
        var currentUserAliveSlots = aliveSlots.Where(x => x.UserId == currentUserId).ToList();
        var clearedDungeonUserIds = await dbContext.UserDungeonClears.Where(clear => clear.DungeonId == room.DungeonId).Select(clear => clear.UserId).ToListAsync();
        var currentUserCharacterIds = slots.Where(slot => slot.UserId == currentUserId && slot.CharacterId.HasValue)
            .Select(slot => slot.CharacterId!.Value).ToList();
        var consumableSlots = await dbContext.CharacterConsumableSlots
            .Where(slot => currentUserCharacterIds.Contains(slot.CharacterId)).ToListAsync();
        var itemStacks = await dbContext.CharacterItemStacks
            .Where(stack => currentUserCharacterIds.Contains(stack.CharacterId)).ToListAsync();
        var cooldowns = await dbContext.BattleConsumableCooldowns
            .Where(entry => entry.RoomId == room.Id && currentUserCharacterIds.Contains(entry.CharacterId)).ToListAsync();
        var skillSlots = await dbContext.CharacterSkillSlots
            .Where(entry => currentUserCharacterIds.Contains(entry.CharacterId)).ToListAsync();
        var skillCooldowns = await dbContext.BattleSkillCooldowns
            .Where(entry => entry.RoomId == room.Id && currentUserCharacterIds.Contains(entry.CharacterId)).ToListAsync();
        var purchasedSkillNodes = (await dbContext.CharacterSkillTalents
            .Where(entry => currentUserCharacterIds.Contains(entry.CharacterId)).ToListAsync())
            .GroupBy(entry => entry.CharacterId)
            .ToDictionary(group => group.Key,
                group => group.Select(entry => entry.NodeCode).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var isCurrentUserAutoUnlocked = currentUserId.HasValue && clearedDungeonUserIds.Contains(currentUserId.Value);
        var isMixedTeam = slots.Any(slot => slot.UserId.HasValue && slot.UserId != room.OwnerUserId);
        var isPreparationTimeoutEnabled = room.IsPreparationTimeoutEnabled;
        var isAllAliveMembersAuto = aliveSlots.Count > 0 && aliveSlots.All(slot => IsSlotAuto(room, slot, clearedDungeonUserIds));
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
            RoomStatus = room.Status, RoundNumber = room.RoundNumber, NextRoundAvailableAtUtc = room.NextRoundAvailableAtUtc, RoundCooldownDurationSeconds = room.RoundCooldownDurationSeconds, PreparationStartedAtUtc = room.PreparationStartedAtUtc, PreparationExpiresAtUtc = isPreparationTimeoutEnabled ? room.PreparationStartedAtUtc?.AddSeconds(BattleRules.PreparationTimeoutSeconds) : null, BattleEndedAtUtc = room.BattleEndedAtUtc,
            IsRepeatBattle = room.IsRepeatBattle, NextBattleStartAtUtc = room.IsRepeatBattle && room.Status == RoomStatus.BattleOver && monster.Hp <= 0 ? room.BattleEndedAtUtc?.AddSeconds(BattleRules.RepeatBattleDelaySeconds) : null,
            NextWaveStartAtUtc = room.Status == RoomStatus.WaveTransition ? room.NextRoundAvailableAtUtc : null,
            ServerTimeUtc = now,
            CanExecuteRound = room.Status == RoomStatus.Preparing && aliveSlots.Count > 0 && aliveSlots.All(x => x.IsConfirmed) && monster.Hp > 0,
            IsMixedTeam = isMixedTeam, IsPreparationTimeoutEnabled = isPreparationTimeoutEnabled, PreparationTimeoutSeconds = BattleRules.PreparationTimeoutSeconds, IsCurrentUserAutoUnlocked = isCurrentUserAutoUnlocked, IsAllAliveMembersAuto = isAllAliveMembersAuto,
            CanPrepare = currentUserAliveSlots.Any(x => !x.IsConfirmed) && !isAllAliveMembersAuto && monster.Hp > 0 &&
                room.Status is RoomStatus.NotStarted or RoomStatus.Preparing &&
                (room.Status == RoomStatus.Preparing || !room.NextRoundAvailableAtUtc.HasValue || room.NextRoundAvailableAtUtc <= now),
            CanLeaveRoom = currentUserId.HasValue && currentUserId != room.OwnerUserId &&
                (room.Status == RoomStatus.BattleOver || room.Status == RoomStatus.NotStarted && room.RoundNumber == 0) &&
                slots.Any(x => x.UserId == currentUserId),
            MonsterIntent = monsterIntent,
            MonsterEffects = monsterEffects,
            BattleLogs = battleLogStore?.Get(room.Id) ?? [],
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
                    ? nodes : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                            HealAmount = item?.HealAmount ?? 0,
                            Quantity = item is null ? 0 : itemStacks.FirstOrDefault(stack => stack.CharacterId == id && stack.ItemCode == item.Code)?.Quantity ?? 0,
                            CooldownRoundsRemaining = Math.Max(0, (cooldown?.ReadyAtRound ?? 0) - room.RoundNumber),
                            AutoUseEnabled = equipped?.AutoUseEnabled ?? false,
                            AutoHpThresholdPercent = equipped?.AutoHpThresholdPercent ?? ConsumableRules.DefaultAutoHpThresholdPercent
                        };
                    }).ToList()
                    : [];
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
                var canConfigureAuto = slot.UserId == currentUserId && isCurrentUserAutoUnlocked && character?.Hp > 0 &&
                    (slot.UserId != room.OwnerUserId || slot.IsMainControl);
                return new RoomSlotResponse { SlotIndex = slot.SlotIndex, CharacterId = slot.CharacterId, PendingConsumableSlotIndex = ownCharacterId.HasValue ? slot.PendingConsumableSlotIndex : null, PendingSkillSlotMask = ownCharacterId.HasValue ? slot.PendingSkillSlotMask : 0, Consumables = ownConsumables, Skills = ownSkills, CharacterName = character?.Name, CharacterElement = characterElement, OutgoingElementModifierPercent = ElementMatchup.PlayerAttackPercent(characterElement, monster.Element), IncomingElementModifierPercent = ElementMatchup.MonsterAttackPercent(monster.Element, characterElement), ProfessionName = character is null ? null : skillCatalog.FindProfession(character.ProfessionCode)?.Name, CharacterHp = character?.Hp, CharacterMaxHp = character is null ? null : TalentRules.EffectiveMaxHp(character), CharacterLevel = character?.Level, CharacterExperience = character?.Experience, ExperienceToNextLevel = character is null ? null : progressionService.GetExperienceToNextLevel(character.Level), TalentPoints = character?.TalentPoints, IsOccupied = slot.CharacterId.HasValue, IsMainControl = slot.IsMainControl, IsCurrentUserCharacter = slot.UserId == currentUserId, IsAlive = character?.Hp > 0, IsConfirmed = slot.IsConfirmed, PlayerName = player?.UserName, IsAutoEnabled = IsSlotAuto(room, slot, clearedDungeonUserIds), IsTemporaryAuto = slot.IsTemporaryAuto, IsAutoUnlockedForCurrentUser = canConfigureAuto, CanConfigureAuto = canConfigureAuto, StatusEffects = character is null ? [] : characterEffects.GetValueOrDefault(character.Id) ?? [] };
            }).ToList()
        };
    }

    private static bool IsSlotAuto(Room room, RoomSlot slot, List<int> clearedDungeonUserIds) =>
        slot.UserId.HasValue && clearedDungeonUserIds.Contains(slot.UserId.Value) &&
        (slot.IsAutoEnabled || (slot.UserId == room.OwnerUserId && !slot.IsMainControl));

    private async Task<RoomSummaryResponse?> BuildRoomSummaryAsync(Room room)
    {
        var dungeon = await dbContext.Dungeons.FindAsync(room.DungeonId);
        var monster = await dbContext.Monsters.FindAsync(room.MonsterId);
        if (monster is null) return null;
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
            IsPreparationTimeoutEnabled = room.IsPreparationTimeoutEnabled
        };
    }

    private List<DungeonRewardPreviewResponse> BuildRewardPreview(Dungeon dungeon)
    {
        var result = new List<DungeonRewardPreviewResponse>();
        var sources = encounterCatalog?.GetRewardSources(dungeon) ?? [new EncounterRewardSource(dungeon.Code, false)];
        foreach (var source in sources)
        {
            var sourceName = source.IsBoss ? "首领掉落" : "怪物掉落";
            result.AddRange(rewardService.GetDropPreview(source.Code, false).Select(drop => new DungeonRewardPreviewResponse
            {
                Source = sourceName, Name = drop.Name, Quantity = drop.Quantity, ChancePercent = drop.ChancePercent
            }));
        }
        result.AddRange(rewardService.GetDropPreview(dungeon.Code, true).Select(drop => new DungeonRewardPreviewResponse
        {
            Source = "通关奖励", Name = drop.Name, Quantity = drop.Quantity, ChancePercent = drop.ChancePercent
        }));
        result.AddRange(rewardService.GetDropPreview($"{dungeon.Code}-first-clear", true).Select(drop => new DungeonRewardPreviewResponse
        {
            Source = "首次通关", Name = drop.Name, Quantity = drop.Quantity, ChancePercent = drop.ChancePercent
        }));
        return result.DistinctBy(item => (item.Source, item.Name, item.Quantity, item.ChancePercent)).ToList();
    }

}
