using Game.Shared.Dtos.Auth;
using Game.Shared.Dtos.Characters;

namespace Game.Client.Services;

/// <summary>Coordinates the account header, selected character and its wallet.</summary>
public sealed class CharacterContextCoordinator : IDisposable
{
    private readonly ApiService _api;
    private readonly ActiveCharacterState _characters;
    private readonly AccountBalanceState _balance;
    private Task<CurrentUserResponse?>? _load;
    private long _generation;

    public CurrentUserResponse? CurrentUser { get; private set; }
    public event Action? Changed;

    public CharacterContextCoordinator(ApiService api, ActiveCharacterState characters, AccountBalanceState balance)
    {
        _api = api;
        _characters = characters;
        _balance = balance;
        api.ContextChanged += Clear;
        balance.Changed += BalanceChanged;
    }

    public Task<CurrentUserResponse?> EnsureLoadedAsync() => LoadAsync(false);
    public Task<CurrentUserResponse?> RefreshAsync() => LoadAsync(true);

    private async Task<CurrentUserResponse?> LoadAsync(bool force)
    {
        await _api.EnsureContextAsync();
        if (_load is { IsCompleted: false }) return await _load;
        _load = ReadAsync(force, _generation);
        return await _load;
    }

    private async Task<CurrentUserResponse?> ReadAsync(bool force, long generation)
    {
        if (force) await _characters.RefreshAsync();
        else await _characters.EnsureLoadedAsync();
        if (generation != _generation) return null;
        var user = await CharacterUserSnapshotReader.LoadAsync(_api, _characters, _balance, force);
        if (generation != _generation) return null;
        // Failed reads retain a known snapshot only within the same context.
        if (user is not null)
        {
            CurrentUser = user;
            if (user.CharacterCount == 0) _characters.Clear();
            BalanceChanged();
            Changed?.Invoke();
        }
        return CurrentUser;
    }

    public async Task<CharacterRosterSnapshot?> LoadRosterAsync()
    {
        await _api.EnsureContextAsync();
        var revision = _api.DataRevision;
        var roster = _api.GetCurrentCharactersAsync();
        var user = EnsureLoadedAsync();
        await Task.WhenAll(roster, user);
        if (revision != _api.DataRevision) return null;
        return new(await roster, await user);
    }

    private void Clear()
    {
        _generation++;
        _load = null;
        CurrentUser = null;
        Changed?.Invoke();
    }

    private void BalanceChanged()
    {
        if (CurrentUser is not { } user || user.ActiveCharacterId != _balance.CharacterId ||
            _balance.CharacterId != _characters.Current?.CharacterId || _balance.Gold is not int gold || user.Gold == gold) return;
        user.Gold = gold;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _api.ContextChanged -= Clear;
        _balance.Changed -= BalanceChanged;
    }
}

public sealed record CharacterRosterSnapshot(List<CharacterSummaryResponse>? Characters, CurrentUserResponse? User);
