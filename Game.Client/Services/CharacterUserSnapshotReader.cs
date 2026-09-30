using Game.Shared.Dtos.Auth;

namespace Game.Client.Services;

public static class CharacterUserSnapshotReader
{
    public static async Task<CurrentUserResponse?> LoadAsync(ApiService api, ActiveCharacterState characters,
        AccountBalanceState balance, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var recoveryAttempted = false;
        for (var attempt = 0; attempt < 3 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            await api.EnsureContextAsync();
            var characterId = characters.Current?.CharacterId;
            var revision = api.DataRevision;
            var user = await api.GetCurrentUserAsync(forceRefresh);
            if (cancellationToken.IsCancellationRequested) return null;
            if (revision != api.DataRevision || characterId != characters.Current?.CharacterId) continue;
            if (user is null || characterId is null) return user;
            if (user.ActiveCharacterId == characterId)
            {
                balance.Update(characterId.Value, user.Gold);
                return user;
            }
            // The server may have switched the active character in another
            // window without changing this token. Never label its gold with
            // the local character ID; recover once and verify both snapshots.
            if (recoveryAttempted) return user;
            recoveryAttempted = true;
            characters.Invalidate();
            await characters.RefreshAsync();
            forceRefresh = true;
        }
        return null;
    }
}
