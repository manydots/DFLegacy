using System.Collections.Concurrent;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

public sealed record SessionSnapshot(
    Guid Id,
    string Service,
    string RemoteEndpoint,
    DateTimeOffset ConnectedAt,
    long ReceivedPackets,
    long SentPackets,
    byte? LastProtocolId);

public sealed record PacketTraceSnapshot(
    DateTimeOffset Timestamp,
    Guid SessionId,
    string Service,
    string Direction,
    byte Type,
    byte ProtocolId,
    int TotalLength,
    byte RecordCount,
    uint DeclaredCrc32,
    bool HasValidCrc32,
    string BodyHex,
    bool BodyTruncated);

public sealed class RuntimeSession
{
    public required Guid Id { get; init; }
    public required string Service { get; init; }
    public required string RemoteEndpoint { get; init; }
    public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.Now;
    public long ReceivedPackets;
    public long SentPackets;
    public byte? LastProtocolId;

    public SessionSnapshot Snapshot() => new(
        Id, Service, RemoteEndpoint, ConnectedAt,
        Interlocked.Read(ref ReceivedPackets),
        Interlocked.Read(ref SentPackets),
        LastProtocolId);
}

public sealed class RuntimeState
{
    private readonly ConcurrentDictionary<Guid, RuntimeSession> _sessions = new();
    private readonly ConcurrentQueue<PacketTraceSnapshot> _recentPackets = new();
    private long _totalConnections;
    private long _totalPackets;
    private const int MaximumRecentPackets = 400;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public long TotalConnections => Interlocked.Read(ref _totalConnections);
    public long TotalPackets => Interlocked.Read(ref _totalPackets);

    public RuntimeSession Add(string service, string remoteEndpoint)
    {
        var session = new RuntimeSession
        {
            Id = Guid.NewGuid(),
            Service = service,
            RemoteEndpoint = remoteEndpoint
        };
        _sessions[session.Id] = session;
        Interlocked.Increment(ref _totalConnections);
        return session;
    }

    public void CountPacket(RuntimeSession session)
    {
        Interlocked.Increment(ref session.ReceivedPackets);
        Interlocked.Increment(ref _totalPackets);
    }

    public void TracePacket(RuntimeSession session, string direction, PacketFrame frame, int dumpLimit)
    {
        var dumpLength = Math.Min(frame.Body.Length, Math.Max(0, dumpLimit));
        _recentPackets.Enqueue(new PacketTraceSnapshot(
            DateTimeOffset.Now,
            session.Id,
            session.Service,
            direction,
            frame.Type,
            frame.ProtocolId,
            frame.TotalLength,
            frame.RecordCount,
            frame.DeclaredCrc32,
            frame.HasValidCrc32,
            Convert.ToHexString(frame.Body.AsSpan(0, dumpLength)),
            frame.Body.Length > dumpLength));

        while (_recentPackets.Count > MaximumRecentPackets)
        {
            _recentPackets.TryDequeue(out _);
        }
    }

    public void TracePacket(RuntimeSession session, string direction, GameServerPacket packet, int dumpLimit)
    {
        var dumpLength = Math.Min(packet.Payload.Length, Math.Max(0, dumpLimit));
        _recentPackets.Enqueue(new PacketTraceSnapshot(
            DateTimeOffset.Now,
            session.Id,
            session.Service,
            direction,
            packet.Type,
            packet.ProtocolId,
            packet.TotalLength,
            packet.Payload.Length == 0 ? (byte)0 : packet.Payload[0],
            0,
            true,
            Convert.ToHexString(packet.Payload.AsSpan(0, dumpLength)),
            packet.Payload.Length > dumpLength));

        while (_recentPackets.Count > MaximumRecentPackets)
        {
            _recentPackets.TryDequeue(out _);
        }
    }

    public void TraceDatagram(
        RuntimeSession session,
        string direction,
        ReadOnlySpan<byte> datagram,
        int dumpLimit,
        byte protocolId = byte.MaxValue)
    {
        var dumpLength = Math.Min(datagram.Length, Math.Max(0, dumpLimit));
        _recentPackets.Enqueue(new PacketTraceSnapshot(
            DateTimeOffset.Now,
            session.Id,
            session.Service,
            direction,
            2,
            protocolId,
            datagram.Length,
            0,
            0,
            true,
            Convert.ToHexString(datagram[..dumpLength]),
            datagram.Length > dumpLength));

        while (_recentPackets.Count > MaximumRecentPackets)
        {
            _recentPackets.TryDequeue(out _);
        }
    }

    public void Remove(Guid id) => _sessions.TryRemove(id, out _);
    public SessionSnapshot[] Sessions() => _sessions.Values.Select(x => x.Snapshot()).ToArray();
    public PacketTraceSnapshot[] RecentPackets() => _recentPackets.ToArray();
}
