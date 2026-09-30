using Game.Shared.Dtos.Characters;

namespace Game.Client.Services;

public sealed class ActiveCharacterState : IDisposable
{
    private readonly ApiService _api;
    private Task? _refresh;
    private long _generation;

    public CurrentCharacterResponse? Current { get; private set; }
    public event Action? Changed;

    public ActiveCharacterState(ApiService apiService)
    {
        _api = apiService;
        _api.ContextChanged += Clear;
    }

    public Task RefreshAsync() => LoadAsync(true);
    public Task EnsureLoadedAsync() => LoadAsync(false);

    private async Task LoadAsync(bool force)
    {
        await _api.EnsureContextAsync();
        if (_refresh is { IsCompleted: false }) { await _refresh; return; }
        _refresh = ReadAsync(force, _generation, _api.DataRevision);
        await _refresh;
    }

    private async Task ReadAsync(bool force, long generation, long context)
    {
        var updated = await _api.GetCurrentCharacterAsync(force);
        if (generation != _generation || context != _api.DataRevision || updated is null) return;
        if (SameCharacter(Current, updated)) return;
        if (Current is { } previous) _api.ObserveCharacterChange(previous, updated);
        Current = updated;
        Changed?.Invoke();
    }

    public void Invalidate()
    {
        _generation++;
        _refresh = null;
        _api.InvalidateCharacter();
    }

    public void Clear()
    {
        Invalidate();
        if (Current is null) return;
        Current = null;
        Changed?.Invoke();
    }

    public void ApplyWeaponSummary(CharacterWeaponsResponse value)
    {
        if (Current is not { } current || current.CharacterId != value.CharacterId) return;
        Invalidate();
        var updated = new CurrentCharacterResponse
        {
            CharacterId = current.CharacterId, Name = current.Name,
            ProfessionCode = current.ProfessionCode, ProfessionName = current.ProfessionName,
            ActiveBattleRoomId = current.ActiveBattleRoomId,
            Hp = value.Hp, MaxHp = value.EffectiveMaxHp, Attack = value.EffectiveAttack,
            Level = current.Level, Experience = current.Experience, ExperienceToNextLevel = current.ExperienceToNextLevel
        };
        _api.ApplyCharacterSummary(updated);
        if (SameCharacter(Current, updated)) return;
        Current = updated;
        Changed?.Invoke();
    }

    public void Dispose() => _api.ContextChanged -= Clear;

    private static bool SameCharacter(CurrentCharacterResponse? left, CurrentCharacterResponse? right) =>
        left is null ? right is null : right is not null &&
        left.CharacterId == right.CharacterId && left.ActiveBattleRoomId == right.ActiveBattleRoomId &&
        left.Name == right.Name && left.ProfessionCode == right.ProfessionCode &&
        left.ProfessionName == right.ProfessionName &&
        left.Hp == right.Hp && left.MaxHp == right.MaxHp &&
        left.Attack == right.Attack &&
        left.Level == right.Level && left.Experience == right.Experience &&
        left.ExperienceToNextLevel == right.ExperienceToNextLevel;
}
