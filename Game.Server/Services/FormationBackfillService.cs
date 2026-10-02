using Game.Server.Data;
using Game.Server.Configuration;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

/// <summary>Run after schema migration and before admitting requests or advancing battles.</summary>
public sealed class FormationBackfillService(GameDbContext db, CombatLoadoutService loadouts, IOptions<FormationOptions>? options = null)
{
    private readonly int _configuredPositions = (options?.Value ?? new FormationOptions()).ValidatedPositions;
    public async Task BackfillAsync()
    {
        foreach (var character in await db.Characters.ToListAsync())
            await BackfillCharacterAsync(character);
    }

    public async Task BackfillCharacterAsync(Character character)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var definition = await loadouts.CaptureAsync(character.Id);
        if (!await db.CharacterFormationStates.AnyAsync(x => x.CharacterId == character.Id))
        {
            var mainId = definition.Weapons.FirstOrDefault(x => x.SlotIndex == 1)?.WeaponId;
            var element = mainId is int id ? (await db.CharacterWeapons.FindAsync(id))?.Element ?? ElementType.Fire : ElementType.Fire;
            var positions = await db.CharacterBattleFormations.Where(x => x.CharacterId == character.Id && !x.IsDeleted)
                .Select(x => new { x.GroupElement, x.Position }).ToListAsync();
            var available = Enum.GetValues<ElementType>().OrderBy(x => x == element ? 0 : 1)
                .SelectMany(x => Enumerable.Range(1, _configuredPositions).Select(position => (Element: x, Position: position)))
                .FirstOrDefault(x => !positions.Any(p => p.GroupElement == x.Element && p.Position == x.Position));
            // A caller can create presets before ever opening the overview. Preserve those occupied positions.
            if (available.Position == 0)
            {
                db.CharacterFormationStates.Add(new CharacterFormationState { CharacterId = character.Id });
            }
            else
            {
                var formation = new CharacterBattleFormation { CharacterId = character.Id };
                FormationService.SetMetadata(formation, "当前配置", available.Element, available.Position, definition);
                FormationService.SetChildren(formation, definition);
                db.CharacterBattleFormations.Add(formation);
                await db.SaveChangesAsync();
                db.CharacterFormationStates.Add(new CharacterFormationState
                {
                    CharacterId = character.Id, AppliedFormationId = formation.Id, AppliedFormationVersion = formation.Version,
                    AppliedChoiceHash = CombatLoadoutCodec.ConfigurationHash(definition)
                });
            }
        }
        foreach (var slot in await db.RoomSlots.Where(x => x.CharacterId == character.Id && x.AppliedLoadoutJson == null).ToListAsync())
        {
            slot.AppliedLoadoutJson = CombatLoadoutCodec.Serialize(definition); slot.SourceFormationName = "LegacyCurrent";
        }
        foreach (var operation in await db.RoomOperations.Where(x => x.CharacterId == character.Id && x.Status == "Pending" && x.RequestedLoadoutJson == null &&
            (x.Kind == RoomOperationKind.Join || x.Kind == RoomOperationKind.Assign) && x.ExpectedSourceSlotIndex == null).ToListAsync())
        {
            operation.RequestedLoadoutJson = CombatLoadoutCodec.Serialize(definition); operation.SourceFormationName = "LegacyCurrent";
        }
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    }
}
