using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class WeaponService(GameDbContext dbContext, UserService userService, SkillCatalog skillCatalog)
{
    public async Task<(CharacterWeaponsResponse? Response, string? Error)> GetAsync(string? token, int characterId)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        return error is null ? (await BuildResponseAsync(character!), null) : (null, error);
    }

    public async Task<(CharacterWeaponsResponse? Response, string? Error)> SetSlotAsync(
        string? token, int characterId, int slotIndex, SetWeaponSlotRequest request)
    {
        var (character, error) = await GetOwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        if (slotIndex is < 1 or > WeaponRules.SlotCount) return (null, "InvalidSlotIndex");
        if (slotIndex == WeaponRules.MainSlotIndex && request.WeaponId is null) return (null, "MainWeaponRequired");

        var roomSlot = await dbContext.RoomSlots.SingleOrDefaultAsync(slot => slot.CharacterId == characterId);
        Room? room = roomSlot is null ? null : await dbContext.Rooms.FindAsync(roomSlot.RoomId);
        if (room is not null && room.Status != RoomStatus.BattleOver &&
            (room.Status != RoomStatus.NotStarted || room.RoundNumber > 0)) return (null, "LoadoutLocked");

        var weapons = await dbContext.CharacterWeapons.Where(weapon => weapon.CharacterId == characterId).ToListAsync();
        var selected = request.WeaponId is int id ? weapons.SingleOrDefault(weapon => weapon.Id == id) : null;
        if (request.WeaponId is not null && selected is null) return (null, "WeaponNotOwned");
        var displaced = weapons.SingleOrDefault(weapon => weapon.EquippedSlotIndex == slotIndex);
        if (selected?.EquippedSlotIndex == WeaponRules.MainSlotIndex &&
            slotIndex != WeaponRules.MainSlotIndex && displaced is null) return (null, "MainWeaponRequired");
        if (selected?.Id == displaced?.Id || selected is null && displaced is null)
            return (await BuildResponseAsync(character!), null);

        var previousSlot = selected?.EquippedSlotIndex;
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            // Release the two unique slot values before assigning their new owners.
            if (selected?.EquippedSlotIndex is not null)
            {
                selected.EquippedSlotIndex = null;
                selected.Version++;
                await dbContext.SaveChangesAsync();
            }
            if (displaced is not null)
            {
                displaced.EquippedSlotIndex = previousSlot;
                displaced.Version++;
                await dbContext.SaveChangesAsync();
            }
            if (selected is not null)
            {
                selected.EquippedSlotIndex = slotIndex;
                selected.Version++;
            }

            var equipped = weapons.Where(weapon => weapon.EquippedSlotIndex is not null).ToList();
            character!.Attack = equipped.Sum(weapon => weapon.Attack);
            character.MaxHp = equipped.Sum(weapon => weapon.MaxHp);
            character.Hp = Math.Min(character.Hp, TalentRules.EffectiveMaxHp(character));
            character.Version++;
            if (room is not null) room.Version++;
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return (await BuildResponseAsync(character), null);
        }
        catch (DbUpdateException)
        {
            return (null, "ConcurrencyConflict");
        }
    }

    private async Task<(Character? Character, string? Error)> GetOwnedCharacterAsync(string? token, int characterId)
    {
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        var character = await dbContext.Characters.SingleOrDefaultAsync(item => item.Id == characterId);
        if (character is null) return (null, "CharacterNotFound");
        return character.UserId == user!.Id ? (character, null) : (null, "NotOwner");
    }

    private async Task<CharacterWeaponsResponse> BuildResponseAsync(Character character)
    {
        var weapons = await dbContext.CharacterWeapons.Where(item => item.CharacterId == character.Id)
            .OrderBy(item => item.EquippedSlotIndex == null).ThenBy(item => item.EquippedSlotIndex)
            .ThenBy(item => item.Id).ToListAsync();
        var main = weapons.SingleOrDefault(item => item.EquippedSlotIndex == WeaponRules.MainSlotIndex);
        return new CharacterWeaponsResponse
        {
            CharacterId = character.Id,
            CharacterName = character.Name,
            ProfessionName = skillCatalog.FindProfession(character.ProfessionCode)?.Name ?? character.ProfessionCode,
            Hp = character.Hp,
            TotalAttack = character.Attack,
            TotalMaxHp = character.MaxHp,
            EffectiveAttack = TalentRules.EffectiveAttack(character),
            EffectiveMaxHp = TalentRules.EffectiveMaxHp(character),
            Defense = TalentRules.EffectiveDefense(character),
            MainElement = main?.Element,
            Weapons = weapons.Select(item => new CharacterWeaponResponse
            {
                Id = item.Id,
                WeaponCode = item.WeaponCode,
                Name = item.Name,
                Element = item.Element,
                Attack = item.Attack,
                MaxHp = item.MaxHp,
                EquippedSlotIndex = item.EquippedSlotIndex
            }).ToList()
        };
    }
}
