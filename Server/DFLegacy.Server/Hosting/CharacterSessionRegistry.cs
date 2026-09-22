using System.Collections.Concurrent;
using System.Net.Sockets;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

internal readonly record struct CharacterSessionKey(Guid StorageId, uint CharacterNo)
{
    public static CharacterSessionKey ForStorageId(Guid storageId) => new(storageId, 0);

    public static CharacterSessionKey ForCharacterNo(uint characterNo) => new(Guid.Empty, characterNo);
}

public sealed class CharacterSessionRegistry(ILogger<CharacterSessionRegistry> logger)
{
    private readonly ConcurrentDictionary<CharacterSessionKey, CharacterSessionLease> _activeCharacters = new();

    public Task<CharacterSessionLease> ClaimAsync(
        Guid characterId,
        Guid sessionId,
        Action kickConnection,
        CancellationToken cancellationToken,
        Action? mailReceived = null,
        Action<byte, uint>? premiumInfo = null,
        Action<uint>? ceraInfo = null,
        Action? fatigueReset = null,
        Action<GameMessageType, ushort, byte[]>? messageNotification = null,
        Action<byte[]>? popupNotification = null,
        Action<byte>? weaknessRecovery = null,
        Action<ushort, byte>? dungeonPermission = null) =>
        ClaimCoreAsync(
            CharacterSessionKey.ForStorageId(characterId),
            characterId,
            characterNo: 0,
            sessionId,
            kickConnection,
            cancellationToken,
            mailReceived,
            premiumInfo,
            ceraInfo,
            fatigueReset,
            messageNotification,
            popupNotification,
            weaknessRecovery,
            dungeonPermission);

    public Task<CharacterSessionLease> ClaimAsync(
        uint characterNo,
        Guid sessionId,
        Action kickConnection,
        CancellationToken cancellationToken,
        Action? mailReceived = null,
        Action<byte, uint>? premiumInfo = null,
        Action<uint>? ceraInfo = null,
        Action? fatigueReset = null,
        Action<GameMessageType, ushort, byte[]>? messageNotification = null,
        Action<byte[]>? popupNotification = null,
        Action<byte>? weaknessRecovery = null,
        Action<ushort, byte>? dungeonPermission = null)
        => ClaimAsync(
            characterNo,
            storageId: Guid.Empty,
            sessionId,
            kickConnection,
            cancellationToken,
            mailReceived,
            premiumInfo,
            ceraInfo,
            fatigueReset,
            messageNotification,
            popupNotification,
            weaknessRecovery,
            dungeonPermission);

    public Task<CharacterSessionLease> ClaimAsync(
        uint characterNo,
        Guid storageId,
        Guid sessionId,
        Action kickConnection,
        CancellationToken cancellationToken,
        Action? mailReceived = null,
        Action<byte, uint>? premiumInfo = null,
        Action<uint>? ceraInfo = null,
        Action? fatigueReset = null,
        Action<GameMessageType, ushort, byte[]>? messageNotification = null,
        Action<byte[]>? popupNotification = null,
        Action<byte>? weaknessRecovery = null,
        Action<ushort, byte>? dungeonPermission = null)
    {
        if (characterNo == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterNo));
        }

        return ClaimCoreAsync(
            CharacterSessionKey.ForCharacterNo(characterNo),
            storageId,
            characterNo,
            sessionId,
            kickConnection,
            cancellationToken,
            mailReceived,
            premiumInfo,
            ceraInfo,
            fatigueReset,
            messageNotification,
            popupNotification,
            weaknessRecovery,
            dungeonPermission);
    }

    private async Task<CharacterSessionLease> ClaimCoreAsync(
        CharacterSessionKey key,
        Guid storageId,
        uint characterNo,
        Guid sessionId,
        Action kickConnection,
        CancellationToken cancellationToken,
        Action? mailReceived,
        Action<byte, uint>? premiumInfo,
        Action<uint>? ceraInfo,
        Action? fatigueReset,
        Action<GameMessageType, ushort, byte[]>? messageNotification,
        Action<byte[]>? popupNotification,
        Action<byte>? weaknessRecovery,
        Action<ushort, byte>? dungeonPermission)
    {
        var candidate = new CharacterSessionLease(
            key,
            storageId,
            characterNo,
            sessionId,
            kickConnection,
            mailReceived,
            premiumInfo,
            ceraInfo,
            fatigueReset,
            messageNotification,
            popupNotification,
            weaknessRecovery,
            dungeonPermission);
        while (true)
        {
            if (_activeCharacters.TryAdd(key, candidate))
            {
                return candidate;
            }

            if (!_activeCharacters.TryGetValue(key, out var previous))
            {
                continue;
            }

            if (previous.SessionId == sessionId)
            {
                return previous;
            }

            if (!_activeCharacters.TryUpdate(key, candidate, previous))
            {
                continue;
            }

            logger.LogWarning(
                "Character session moved: CharacterNo={CharacterNo}, StorageId={StorageId}, old session={OldSessionId}, new session={NewSessionId}; disconnecting the old session.",
                characterNo,
                storageId,
                previous.SessionId,
                sessionId);
            previous.Kick();

            try
            {
                await previous.Released.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return candidate;
            }
            catch
            {
                Release(candidate);
                throw;
            }
        }
    }

    public void Release(CharacterSessionLease? lease)
    {
        if (lease is null)
        {
            return;
        }

        _activeCharacters.TryRemove(
            new KeyValuePair<CharacterSessionKey, CharacterSessionLease>(lease.Key, lease));
        lease.MarkReleased();
    }

    public bool NotifyMailReceived(Guid characterId)
    {
        if (_activeCharacters.TryGetValue(
                CharacterSessionKey.ForStorageId(characterId),
                out var storageLease))
        {
            storageLease.NotifyMailReceived();
            return true;
        }

        var numericLease = _activeCharacters.Values.FirstOrDefault(lease =>
            lease.CharacterId == characterId);
        if (numericLease is null)
        {
            return false;
        }

        numericLease.NotifyMailReceived();
        return true;
    }

    public bool NotifyMailReceived(uint characterNo)
    {
        if (characterNo == 0)
        {
            return false;
        }

        return NotifyMailReceived(CharacterSessionKey.ForCharacterNo(characterNo));
    }

    private bool NotifyMailReceived(CharacterSessionKey key)
    {
        if (!_activeCharacters.TryGetValue(key, out var lease))
        {
            return false;
        }

        lease.NotifyMailReceived();
        return true;
    }

    public bool NotifyPremiumInfo(Guid characterId, byte serviceType, uint remainSeconds)
    {
        if (_activeCharacters.TryGetValue(
                CharacterSessionKey.ForStorageId(characterId),
                out var storageLease))
        {
            storageLease.NotifyPremiumInfo(serviceType, remainSeconds);
            return true;
        }

        var numericLease = _activeCharacters.Values.FirstOrDefault(lease =>
            lease.CharacterId == characterId);
        if (numericLease is null)
        {
            return false;
        }

        numericLease.NotifyPremiumInfo(serviceType, remainSeconds);
        return true;
    }

    public bool NotifyCera(Guid characterId, uint value)
    {
        if (_activeCharacters.TryGetValue(
                CharacterSessionKey.ForStorageId(characterId),
                out var storageLease))
        {
            storageLease.NotifyCera(value);
            return true;
        }

        var numericLease = _activeCharacters.Values.FirstOrDefault(lease =>
            lease.CharacterId == characterId);
        if (numericLease is null)
        {
            return false;
        }

        numericLease.NotifyCera(value);
        return true;
    }

    public bool NotifyFatigueReset(Guid characterId)
    {
        if (_activeCharacters.TryGetValue(
                CharacterSessionKey.ForStorageId(characterId),
                out var storageLease))
        {
            storageLease.NotifyFatigueReset();
            return true;
        }

        var numericLease = _activeCharacters.Values.FirstOrDefault(lease =>
            lease.CharacterId == characterId);
        if (numericLease is null)
        {
            return false;
        }

        numericLease.NotifyFatigueReset();
        return true;
    }

    public bool NotifyWeaknessRecovery(Guid characterId, byte recovery)
    {
        if (_activeCharacters.TryGetValue(
                CharacterSessionKey.ForStorageId(characterId),
                out var storageLease))
        {
            storageLease.NotifyWeaknessRecovery(recovery);
            return true;
        }

        var numericLease = _activeCharacters.Values.FirstOrDefault(lease =>
            lease.CharacterId == characterId);
        if (numericLease is null)
        {
            return false;
        }

        numericLease.NotifyWeaknessRecovery(recovery);
        return true;
    }

    public bool NotifyMessage(
        Guid characterId,
        GameMessageType messageType,
        ushort targetAreaUserId,
        byte[] messageBytes)
    {
        ArgumentNullException.ThrowIfNull(messageBytes);

        if (_activeCharacters.TryGetValue(
                CharacterSessionKey.ForStorageId(characterId),
                out var storageLease))
        {
            storageLease.NotifyMessage(messageType, targetAreaUserId, messageBytes);
            return true;
        }

        var numericLease = _activeCharacters.Values.FirstOrDefault(lease =>
            lease.CharacterId == characterId);
        if (numericLease is null)
        {
            return false;
        }

        numericLease.NotifyMessage(messageType, targetAreaUserId, messageBytes);
        return true;
    }

    public bool NotifyPopup(Guid characterId, byte[] messageBytes)
    {
        ArgumentNullException.ThrowIfNull(messageBytes);

        if (_activeCharacters.TryGetValue(
                CharacterSessionKey.ForStorageId(characterId),
                out var storageLease))
        {
            storageLease.NotifyPopup(messageBytes);
            return true;
        }

        var numericLease = _activeCharacters.Values.FirstOrDefault(lease =>
            lease.CharacterId == characterId);
        if (numericLease is null)
        {
            return false;
        }

        numericLease.NotifyPopup(messageBytes);
        return true;
    }

    public bool NotifyDungeonPermission(
        Guid characterId,
        ushort dungeonId,
        byte maximumDifficulty)
    {
        var lease = FindByCharacterId(characterId);
        if (lease is null)
        {
            return false;
        }

        lease.NotifyDungeonPermission(dungeonId, maximumDifficulty);
        return true;
    }

    public int NotifyMessageAll(
        GameMessageType messageType,
        ushort targetAreaUserId,
        byte[] messageBytes)
    {
        ArgumentNullException.ThrowIfNull(messageBytes);
        var leases = SnapshotDistinctSessions();
        foreach (var lease in leases)
        {
            lease.NotifyMessage(messageType, targetAreaUserId, messageBytes);
        }

        return leases.Length;
    }

    public int NotifyPopupAll(byte[] messageBytes)
    {
        ArgumentNullException.ThrowIfNull(messageBytes);
        var leases = SnapshotDistinctSessions();
        foreach (var lease in leases)
        {
            lease.NotifyPopup(messageBytes);
        }

        return leases.Length;
    }

    public bool IsOnline(Guid characterId) => FindByCharacterId(characterId) is not null;

    private CharacterSessionLease? FindByCharacterId(Guid characterId)
    {
        if (_activeCharacters.TryGetValue(
                CharacterSessionKey.ForStorageId(characterId),
                out var storageLease))
        {
            return storageLease;
        }

        return _activeCharacters.Values.FirstOrDefault(lease =>
            lease.CharacterId == characterId);
    }

    private CharacterSessionLease[] SnapshotDistinctSessions() =>
        _activeCharacters.Values
            .GroupBy(lease => lease.SessionId)
            .Select(group => group.First())
            .ToArray();

}

public sealed class CharacterSessionLease
{
    private readonly Action _kickConnection;
    private readonly Action? _mailReceived;
    private readonly Action<byte, uint>? _premiumInfo;
    private readonly Action<uint>? _ceraInfo;
    private readonly Action? _fatigueReset;
    private readonly Action<GameMessageType, ushort, byte[]>? _messageNotification;
    private readonly Action<byte[]>? _popupNotification;
    private readonly Action<byte>? _weaknessRecovery;
    private readonly Action<ushort, byte>? _dungeonPermission;
    private readonly TaskCompletionSource _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _kickIssued;

    internal CharacterSessionLease(
        CharacterSessionKey key,
        Guid characterId,
        uint characterNo,
        Guid sessionId,
        Action kickConnection,
        Action? mailReceived,
        Action<byte, uint>? premiumInfo,
        Action<uint>? ceraInfo,
        Action? fatigueReset,
        Action<GameMessageType, ushort, byte[]>? messageNotification,
        Action<byte[]>? popupNotification,
        Action<byte>? weaknessRecovery,
        Action<ushort, byte>? dungeonPermission)
    {
        Key = key;
        CharacterId = characterId;
        CharacterNo = characterNo;
        SessionId = sessionId;
        _kickConnection = kickConnection;
        _mailReceived = mailReceived;
        _premiumInfo = premiumInfo;
        _ceraInfo = ceraInfo;
        _fatigueReset = fatigueReset;
        _messageNotification = messageNotification;
        _popupNotification = popupNotification;
        _weaknessRecovery = weaknessRecovery;
        _dungeonPermission = dungeonPermission;
    }

    public Guid CharacterId { get; }
    public uint CharacterNo { get; }
    public Guid SessionId { get; }
    public Task Released => _released.Task;

    internal CharacterSessionKey Key { get; }

    internal void Kick()
    {
        if (Interlocked.Exchange(ref _kickIssued, 1) != 0)
        {
            return;
        }

        try
        {
            _kickConnection();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    internal void MarkReleased() => _released.TrySetResult();

    internal void NotifyMailReceived() => _mailReceived?.Invoke();

    internal void NotifyPremiumInfo(byte serviceType, uint remainSeconds) =>
        _premiumInfo?.Invoke(serviceType, remainSeconds);

    internal void NotifyCera(uint value) => _ceraInfo?.Invoke(value);

    internal void NotifyFatigueReset() => _fatigueReset?.Invoke();

    internal void NotifyWeaknessRecovery(byte recovery) =>
        _weaknessRecovery?.Invoke(recovery);

    internal void NotifyMessage(
        GameMessageType messageType,
        ushort targetAreaUserId,
        byte[] messageBytes) =>
        _messageNotification?.Invoke(messageType, targetAreaUserId, messageBytes.ToArray());

    internal void NotifyPopup(byte[] messageBytes) =>
        _popupNotification?.Invoke(messageBytes.ToArray());

    internal void NotifyDungeonPermission(ushort dungeonId, byte maximumDifficulty) =>
        _dungeonPermission?.Invoke(dungeonId, maximumDifficulty);
}
