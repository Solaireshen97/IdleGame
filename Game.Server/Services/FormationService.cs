using Game.Server.Data;
using Game.Shared;
using Game.Server.Configuration;
using System.Text.Json;
using Game.Shared.Dtos.Formations;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class FormationService(GameDbContext db, UserService users, CombatLoadoutService loadouts, IOptions<FormationOptions>? options = null)
{
    private readonly int _configuredPositions = (options?.Value ?? new FormationOptions()).ValidatedPositions;

    public async Task<(FormationOverviewResponse? Response, string? Error)> GetAsync(string? token, int characterId)
    {
        var (character, error) = await OwnedAsync(token, characterId);
        if (error is not null) return (null, error);
        try
        {
            if (!await db.CharacterFormationStates.AnyAsync(x => x.CharacterId == characterId))
                await new FormationBackfillService(db, loadouts, options).BackfillCharacterAsync(character!);
            return (await OverviewAsync(character!), null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        { db.ChangeTracker.Clear(); return (null, "ConcurrencyConflict"); }
    }

    public async Task<(FormationResponse? Response, string? Error)> GetOneAsync(string? token, int characterId, int id)
    {
        var (character, error) = await OwnedAsync(token, characterId);
        if (error is not null) return (null, error);
        var formation = await FindAsync(characterId, id);
        return formation is null ? (null, "FormationNotFound") : (await ResponseAsync(character!, formation), null);
    }

    public async Task<(FormationResponse? Response, string? Error)> CreateAsync(string? token, int characterId, CreateFormationRequest request)
    {
        var (character, error) = await OwnedAsync(token, characterId);
        if (error is not null) return (null, error);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var definition = request.FromCurrent ? await loadouts.CaptureAsync(characterId) : request.Loadout;
        error = await ValidateSaveAsync(character!, request.Name, request.GroupElement, request.Position, definition);
        if (error is not null) return (null, error);
        try
        {
            var formation = new CharacterBattleFormation { CharacterId = characterId };
            SetMetadata(formation, request.Name, request.GroupElement, request.Position, definition);
            SetChildren(formation, definition);
            db.CharacterBattleFormations.Add(formation);
            character!.Version++;
            await db.SaveChangesAsync();
            await StoryProgressService.RecordAsync(db, characterId, "FormationSaved", "",
                $"formation:{formation.Id}:{formation.Version}", 1, DateTime.UtcNow);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return (await ResponseAsync(character!, formation), null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(FormationResponse? Response, string? Error)> SaveAsync(string? token, int characterId, int id, SaveFormationRequest request)
    {
        var (character, error) = await OwnedAsync(token, characterId);
        if (error is not null) return (null, error);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var formation = await FindAsync(characterId, id);
        if (formation is null) return (null, "FormationNotFound");
        if (request.ExpectedVersion != formation.Version) return (null, "ConcurrencyConflict");
        error = await ValidateSaveAsync(character!, request.Name, request.GroupElement, request.Position, request.Loadout);
        if (error is not null) return (null, error);
        try
        {
            // Flush removal first: replacement entities use the same composite slot keys.
            db.FormationWeaponSlots.RemoveRange(formation.Weapons);
            db.FormationSkillSlots.RemoveRange(formation.Skills);
            db.FormationConsumableSlots.RemoveRange(formation.Consumables);
            await db.SaveChangesAsync();
            formation.Weapons.Clear(); formation.Skills.Clear(); formation.Consumables.Clear();
            SetMetadata(formation, request.Name, request.GroupElement, request.Position, request.Loadout);
            SetChildren(formation, request.Loadout);
            formation.Version++;
            character!.Version++;
            await StoryProgressService.RecordAsync(db, characterId, "FormationSaved", "",
                $"formation:{formation.Id}:{formation.Version}", 1, DateTime.UtcNow);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return (await ResponseAsync(character!, formation), null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(FormationResponse? Response, string? Error)> CopyAsync(string? token, int characterId, int id, CopyFormationRequest request)
    {
        var (source, error) = await GetOneAsync(token, characterId, id);
        if (error is not null) return (null, error);
        if (source!.Version != request.ExpectedVersion) return (null, "ConcurrencyConflict");
        return await CreateAsync(token, characterId, new CreateFormationRequest
        {
            Name = request.Name, GroupElement = request.GroupElement, Position = request.Position, Loadout = source.Loadout
        });
    }

    public async Task<(FormationOverviewResponse? Response, string? Error)> DeleteAsync(string? token, int characterId, int id, int expectedVersion)
    {
        var (character, error) = await OwnedAsync(token, characterId);
        if (error is not null) return (null, error);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var formation = await FindAsync(characterId, id);
        if (formation is null) return (null, "FormationNotFound");
        if (formation.Version != expectedVersion) return (null, "ConcurrencyConflict");
        formation.IsDeleted = true; formation.Version++; formation.UpdatedAtUtc = DateTime.UtcNow;
        db.FormationWeaponSlots.RemoveRange(formation.Weapons);
        db.FormationSkillSlots.RemoveRange(formation.Skills);
        db.FormationConsumableSlots.RemoveRange(formation.Consumables);
        formation.SoulImprintId = null;
        formation.SoulAutoUseEnabled = false;
        formation.SoulAutoConditionOverride = null;
        formation.SoulAutoHpThresholdPercent = SkillRules.DefaultAutoHpThresholdPercent;
        character!.Version++;
        var state = await db.CharacterFormationStates.FindAsync(characterId);
        if (state?.DefaultFormationId == id) { state.DefaultFormationId = null; state.Version++; }
        db.CharacterBattleFormationPreferences.RemoveRange(await db.CharacterBattleFormationPreferences
            .Where(x => x.CharacterId == characterId && x.FormationId == id).ToListAsync());
        try
        {
            await db.SaveChangesAsync(); await transaction.CommitAsync();
            return (await OverviewAsync(character!), null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(FormationOverviewResponse? Response, string? Error)> SetDefaultAsync(string? token, int characterId, SetDefaultFormationRequest request)
    {
        var (character, error) = await OwnedAsync(token, characterId);
        if (error is not null) return (null, error);
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (request.FormationId is int id)
        {
            var formation = await FindAsync(characterId, id);
            if (formation is null) return (null, "FormationNotFound");
            if (request.ExpectedVersion != formation.Version) return (null, "ConcurrencyConflict");
        }
        var state = await StateAsync(characterId);
        state.DefaultFormationId = request.FormationId; state.Version++;
        try
        {
            await db.SaveChangesAsync(); await transaction.CommitAsync(); return (await OverviewAsync(character!), null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(FormationPreviewResponse? Response, string? Error)> PreviewAsync(string? token, int characterId, CombatLoadoutDefinition definition, ElementType? groupElement)
    {
        var (character, error) = await OwnedAsync(token, characterId);
        if (error is null && definition is null) return (null, "InvalidLoadout");
        return error is null ? (await loadouts.PreviewAsync(character!, definition, groupElement), null) : (null, error);
    }

    public async Task<(FormationOverviewResponse? Response, string? Error)> ApplyAsync(string? token, int characterId, int id, ApplyFormationRequest request)
    {
        var (character, error) = await OwnedAsync(token, characterId);
        if (error is not null) return (null, error);
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 100) return (null, "InvalidRequestId");
        var fingerprint = $"Apply:{characterId}:{id}:{request.ExpectedVersion}:{request.ExpectedCharacterVersion}";
        await using var transaction = await db.Database.BeginTransactionAsync();
        var receipt = await db.BattleAdmissionReceipts.FindAsync(character!.UserId, request.RequestId);
        if (receipt is not null)
        {
            if (receipt.Action != "ApplyFormation" || receipt.Fingerprint != fingerprint) return (null, "RequestIdConflict");
            if (receipt.ResultJson is null) return (null, "InvalidRequestReceipt");
            try { return (JsonSerializer.Deserialize<FormationOverviewResponse>(receipt.ResultJson), null); }
            catch (JsonException) { return (null, "InvalidRequestReceipt"); }
        }
        if (character.Version != request.ExpectedCharacterVersion) return (null, "ConcurrencyConflict");
        if (await CharacterActivityManager.IsBusyAsync(db, characterId) || await db.RoomSlots.AnyAsync(x => x.CharacterId == characterId))
            return (null, "CharacterBusy");
        var formation = await FindAsync(characterId, id);
        if (formation is null) return (null, "FormationNotFound");
        if (formation.Version != request.ExpectedVersion) return (null, "ConcurrencyConflict");
        try
        {
            var definition = Definition(formation);
            error = await loadouts.ApplyAsync(character, definition);
            if (error is not null) { await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return (null, error); }
            var state = await StateAsync(characterId);
            state.AppliedFormationId = formation.Id; state.AppliedFormationVersion = formation.Version;
            state.AppliedChoiceHash = CombatLoadoutCodec.ConfigurationHash(definition); state.Version++;
            var response = await OverviewAsync(character);
            db.BattleAdmissionReceipts.Add(new BattleAdmissionReceipt
            {
                UserId = character.UserId, RequestId = request.RequestId, CharacterId = characterId,
                Action = "ApplyFormation", Fingerprint = fingerprint, ResultJson = JsonSerializer.Serialize(response)
            });
            await db.SaveChangesAsync(); await transaction.CommitAsync(); return (response, null);
        }
        catch (UnsupportedCombatSkillLoadoutVersionException)
        {
            await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return (null, "UnsupportedSkillLoadoutVersion");
        }
        catch (FormatException exception)
        {
            await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return (null, exception.Message);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return (null, "ConcurrencyConflict");
        }
    }

    public async Task<(FormationRecommendationResponse? Response, string? Error)> RecommendAsync(string? token, int characterId, string dungeonCode, int depthLevel)
    {
        var (character, error) = await OwnedAsync(token, characterId);
        if (error is not null) return (null, error);
        var dungeon = await db.Dungeons.AsNoTracking().SingleOrDefaultAsync(x => x.Code == dungeonCode);
        if (dungeon is null || depthLevel < 1) return (null, "InvalidEncounter");
        var preference = await db.CharacterBattleFormationPreferences.FindAsync(characterId, dungeon.Code, depthLevel);
        var state = await db.CharacterFormationStates.FindAsync(characterId);
        var candidates = new (int? Id, string Source)[]
        {
            (preference?.FormationId, "Encounter"), (state?.DefaultFormationId, "Default"), (state?.AppliedFormationId, "LastApplied")
        };
        var issues = new List<FormationIssue>();
        foreach (var candidate in candidates.Where(x => x.Id.HasValue).DistinctBy(x => x.Id))
        {
            var formation = await FindAsync(characterId, candidate.Id!.Value);
            if (formation is null) continue;
            var preview = await loadouts.PreviewAsync(character!, Definition(formation), formation.GroupElement);
            if (preview.CanDeploy)
                return (new FormationRecommendationResponse
                {
                    Source = candidate.Source, Issues = issues,
                    Selection = new LoadoutSelection { Mode = "SavedFormation", FormationId = formation.Id, ExpectedVersion = formation.Version }
                }, null);
            issues.AddRange(preview.Issues);
        }
        var current = await loadouts.PreviewAsync(character!, await loadouts.CaptureAsync(characterId));
        issues.AddRange(current.Issues);
        return (new FormationRecommendationResponse { Issues = issues }, null);
    }

    public async Task<(FormationOverviewResponse? Response, string? Error)> ClearMemoryAsync(string? token, int characterId, string dungeonCode, int depthLevel)
    {
        var (character, error) = await OwnedAsync(token, characterId);
        if (error is not null) return (null, error);
        var preference = await db.CharacterBattleFormationPreferences.FindAsync(characterId, dungeonCode, depthLevel);
        if (preference is not null) db.CharacterBattleFormationPreferences.Remove(preference);
        try { await db.SaveChangesAsync(); return (await OverviewAsync(character!), null); }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        { db.ChangeTracker.Clear(); return (null, "ConcurrencyConflict"); }
    }

    public static CombatLoadoutDefinition Definition(CharacterBattleFormation formation) => new()
    {
        ProfessionCode = formation.ProfessionCode, SoulImprintId = formation.SoulImprintId,
        SoulAutoUseEnabled = formation.SoulAutoUseEnabled,
        SoulAutoConditionOverride = formation.SoulAutoConditionOverride, SoulAutoHpThresholdPercent = formation.SoulAutoHpThresholdPercent,
        Weapons = formation.Weapons.OrderBy(x => x.SlotIndex).Select(x => new FormationWeaponChoice { SlotIndex = x.SlotIndex, WeaponId = x.WeaponId }).ToList(),
        Skills = formation.Skills.OrderBy(x => x.SlotIndex).Select(x => new FormationSkillChoice
        {
            SlotIndex = x.SlotIndex, SkillCode = x.SkillCode, AutoUseEnabled = x.AutoUseEnabled,
            AutoConditionOverride = x.AutoConditionOverride, AutoHpThresholdPercent = x.AutoHpThresholdPercent
        }).ToList(),
        Consumables = formation.Consumables.OrderBy(x => x.SlotIndex).Select(x => new FormationConsumableChoice
        {
            SlotIndex = x.SlotIndex, ItemCode = x.ItemCode, AutoUseEnabled = x.AutoUseEnabled,
            AutoConditionOverride = x.AutoConditionOverride, AutoHpThresholdPercent = x.AutoHpThresholdPercent
        }).ToList()
    };

    internal static void SetMetadata(CharacterBattleFormation formation, string name, ElementType element, int position, CombatLoadoutDefinition definition)
    {
        formation.Name = name.Trim(); formation.GroupElement = element; formation.Position = position;
        formation.ProfessionCode = definition.ProfessionCode.Trim(); formation.SoulImprintId = definition.SoulImprintId;
        formation.SoulAutoUseEnabled = definition.SoulAutoUseEnabled; formation.UpdatedAtUtc = DateTime.UtcNow;
        formation.SoulAutoConditionOverride = SkillAutoRules.Normalize(definition.SoulAutoConditionOverride);
        formation.SoulAutoHpThresholdPercent = definition.SoulAutoHpThresholdPercent;
    }
    internal static void SetChildren(CharacterBattleFormation formation, CombatLoadoutDefinition definition)
    {
        formation.Weapons = definition.Weapons.Select(x => new FormationWeaponSlot { FormationId = formation.Id, SlotIndex = x.SlotIndex, WeaponId = x.WeaponId }).ToList();
        formation.Skills = definition.Skills.Select(x => new FormationSkillSlot
        {
            FormationId = formation.Id, SlotIndex = x.SlotIndex, SkillCode = x.SkillCode, AutoUseEnabled = x.AutoUseEnabled,
            AutoConditionOverride = x.AutoConditionOverride, AutoHpThresholdPercent = x.AutoHpThresholdPercent
        }).ToList();
        formation.Consumables = definition.Consumables.Select(x => new FormationConsumableSlot
        {
            FormationId = formation.Id, SlotIndex = x.SlotIndex, ItemCode = x.ItemCode,
            AutoUseEnabled = x.AutoUseEnabled, AutoConditionOverride = SkillAutoRules.Normalize(x.AutoConditionOverride), AutoHpThresholdPercent = x.AutoHpThresholdPercent
        }).ToList();
    }
    private async Task<string?> ValidateSaveAsync(Character character, string name, ElementType group, int position, CombatLoadoutDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 40) return "InvalidFormationName";
        if (!Enum.IsDefined(group) || position < 1 || position > await CapacityAsync(character.Id)) return "InvalidFormationPosition";
        if (definition is null || definition.SchemaVersion != 1) return "UnsupportedLoadoutVersion";
        var preview = await loadouts.PreviewAsync(character, definition, group);
        // A missing main hand is a valid incomplete draft. Catalog removals remain visible in previews.
        var issue = preview.Issues.FirstOrDefault(x => x.Severity == "Error" && x.Code is not ("MainWeaponRequired" or "SkillNotLearned" or "UnknownSkill"));
        return issue?.Code;
    }
    private async Task<(Character? Character, string? Error)> OwnedAsync(string? token, int characterId)
    {
        var (user, error) = await users.GetCurrentUserEntityAsync(token);
        return error is null ? await new CharacterAccessResolver(db).OwnedAsync(user!, characterId) : (null, error);
    }
    private Task<CharacterBattleFormation?> FindAsync(int characterId, int id) => db.CharacterBattleFormations
        .Include(x => x.Weapons).Include(x => x.Skills).Include(x => x.Consumables)
        .SingleOrDefaultAsync(x => x.CharacterId == characterId && x.Id == id && !x.IsDeleted);
    private async Task<CharacterFormationState> StateAsync(int characterId)
    {
        var state = await db.CharacterFormationStates.FindAsync(characterId);
        if (state is not null) return state;
        state = new CharacterFormationState { CharacterId = characterId }; db.CharacterFormationStates.Add(state); return state;
    }
    private async Task<FormationResponse> ResponseAsync(Character character, CharacterBattleFormation formation)
    {
        var definition = Definition(formation);
        return new FormationResponse
        {
            Id = formation.Id, CharacterId = formation.CharacterId, Name = formation.Name, GroupElement = formation.GroupElement,
            Position = formation.Position, Version = formation.Version, Loadout = definition,
            Preview = await loadouts.PreviewAsync(character, definition, formation.GroupElement)
        };
    }
    private async Task<FormationOverviewResponse> OverviewAsync(Character character)
    {
        var definitions = await db.CharacterBattleFormations.AsNoTracking().Include(x => x.Weapons).Include(x => x.Skills).Include(x => x.Consumables)
            .Where(x => x.CharacterId == character.Id && !x.IsDeleted).OrderBy(x => x.GroupElement).ThenBy(x => x.Position).ToListAsync();
        var current = await loadouts.CaptureAsync(character.Id);
        var state = await db.CharacterFormationStates.FindAsync(character.Id);
        var response = new FormationOverviewResponse
        {
            CharacterId = character.Id, CharacterVersion = character.Version, CurrentLoadout = current,
            PositionsPerElement = Math.Max(_configuredPositions, definitions.Select(x => x.Position).DefaultIfEmpty(0).Max()),
            CanApply = !await CharacterActivityManager.IsBusyAsync(db, character.Id) && !await db.RoomSlots.AnyAsync(x => x.CharacterId == character.Id),
            DefaultFormationId = state?.DefaultFormationId, AppliedFormationId = state?.AppliedFormationId,
            AppliedFormationVersion = state?.AppliedFormationVersion,
            CurrentIsModified = state?.AppliedChoiceHash is string hash && hash != CombatLoadoutCodec.ConfigurationHash(current)
        };
        var choices = definitions.Select(x => (Definition: Definition(x), Group: (ElementType?)x.GroupElement)).ToList();
        var previews = await loadouts.PreviewManyAsync(character, choices);
        for (var index = 0; index < definitions.Count; index++)
        {
            var formation = definitions[index];
            response.Formations.Add(new FormationResponse
            {
                Id = formation.Id, CharacterId = formation.CharacterId, Name = formation.Name,
                GroupElement = formation.GroupElement, Position = formation.Position, Version = formation.Version,
                Loadout = choices[index].Definition, Preview = previews[index]
            });
        }
        return response;
    }
    private async Task<int> CapacityAsync(int characterId) => Math.Max(_configuredPositions,
        await db.CharacterBattleFormations.Where(x => x.CharacterId == characterId && !x.IsDeleted).Select(x => (int?)x.Position).MaxAsync() ?? 0);
}
