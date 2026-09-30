using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

/// <summary>Resolves ownership and the selected character without changing or saving account state.</summary>
public sealed class CharacterAccessResolver(GameDbContext db)
{
    public Task<int?> ActiveIdAsync(User user) => db.Characters
        .Where(character => character.UserId == user.Id)
        .OrderByDescending(character => character.Id == user.ActiveCharacterId)
        .ThenBy(character => character.Id)
        .Select(character => (int?)character.Id).FirstOrDefaultAsync();

    public Task<Character?> ActiveAsync(User user) => db.Characters
        .Where(character => character.UserId == user.Id)
        .OrderByDescending(character => character.Id == user.ActiveCharacterId)
        .ThenBy(character => character.Id)
        .FirstOrDefaultAsync();

    public static Character? Active(User user, IReadOnlyList<Character> characters) =>
        characters.FirstOrDefault(character => character.Id == user.ActiveCharacterId && character.UserId == user.Id)
        ?? characters.Where(character => character.UserId == user.Id).MinBy(character => character.Id);

    public async Task<(Character? Character, string? Error)> OwnedAsync(User user, int characterId)
    {
        var character = await db.Characters.FirstOrDefaultAsync(item => item.Id == characterId);
        if (character is null) return (null, "CharacterNotFound");
        return character.UserId == user.Id ? (character, null) : (null, "NotOwner");
    }
}
