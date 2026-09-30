using System.Text.Json;
using Game.Shared.Dtos;
using Microsoft.Extensions.Caching.Memory;

namespace Game.Server.Services;

// A conservative process-wide revision also covers changes outside a room (inventory,
// profession and progress). A future per-room revision may reduce unrelated eviction.
public sealed class RoomProjectionRevision
{
    private long _value;
    public long Value => Interlocked.Read(ref _value);
    public void Changed() => Interlocked.Increment(ref _value);
}

public sealed class RoomProjectionCache(RoomProjectionRevision revision) : IDisposable
{
    private readonly MemoryCache _entries = new(new MemoryCacheOptions { SizeLimit = 256 });
    public long Revision => revision.Value;

    public sealed record Key(int RoomId, int UserId, int? CharacterId);
    private sealed record Entry(long Revision, int RoomVersion, int OfflineMask, string Id, byte[] Json);

    public (RoomDetailResponse Room, string Id)? Get(Key key, int roomVersion, int offlineMask)
    {
        if (!_entries.TryGetValue(key, out Entry? entry) || entry is null ||
            entry.Revision != Revision || entry.RoomVersion != roomVersion || entry.OfflineMask != offlineMask)
            return null;
        return (JsonSerializer.Deserialize<RoomDetailResponse>(entry.Json)!, entry.Id);
    }

    public string Store(Key key, RoomDetailResponse room, int offlineMask, long capturedRevision)
    {
        // Keep histories out of the cached object. They are published after commit and
        // may change without a room/database revision. Each caller gets a private copy.
        var copy = JsonSerializer.Deserialize<RoomDetailResponse>(JsonSerializer.SerializeToUtf8Bytes(room))!;
        copy.BattleEvents = [];
        copy.BattleLogs = [];
        var id = Guid.NewGuid().ToString("N");
        if (capturedRevision != Revision) return "";
        _entries.Set(key, new Entry(capturedRevision, room.RoomVersion, offlineMask, id,
            JsonSerializer.SerializeToUtf8Bytes(copy)), new MemoryCacheEntryOptions
            { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10) });
        return id;
    }

    public void Dispose() => _entries.Dispose();
}
