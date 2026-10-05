using System.Collections.Concurrent;

namespace MbaLms.Api.Infrastructure.Import;

/// <summary>
/// In-memory preview sessions for a single API instance. TTL 30 minutes.
/// Take() removes the session atomically so confirm is one-shot (success or failure).
/// </summary>
public sealed class ImportSessionStore
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, ImportSession> _sessions = new(StringComparer.Ordinal);

    public string Save(Guid managerUserId, string entity, object payload)
    {
        var id = Guid.NewGuid().ToString("N");
        var session = new ImportSession
        {
            ImportId = id,
            ManagerUserId = managerUserId,
            Entity = entity,
            ExpiresAt = DateTimeOffset.UtcNow.Add(Ttl),
            Payload = payload
        };
        if (!_sessions.TryAdd(id, session))
            throw new InvalidOperationException("Failed to allocate import id.");
        return id;
    }

    /// <summary>
    /// Returns the session without removing it. Wrong owner/entity → null (session kept).
    /// Expired → removed and null.
    /// </summary>
    public ImportSession? TryGet(string importId, Guid managerUserId, string entity)
    {
        if (!_sessions.TryGetValue(importId, out var session)) return null;
        if (session.ManagerUserId != managerUserId || !string.Equals(session.Entity, entity, StringComparison.Ordinal))
            return null;
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(importId, out _);
            return null;
        }
        return session;
    }

    /// <summary>
    /// Atomically removes and returns the session for confirm. Parallel callers: only one succeeds.
    /// Wrong owner/entity → null without removing. Missing/expired/already taken → null.
    /// </summary>
    public ImportSession? Take(string importId, Guid managerUserId, string entity)
    {
        if (!_sessions.TryGetValue(importId, out var existing)) return null;
        if (existing.ManagerUserId != managerUserId || !string.Equals(existing.Entity, entity, StringComparison.Ordinal))
            return null;
        if (existing.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(importId, out _);
            return null;
        }
        if (!_sessions.TryRemove(importId, out var session)) return null;
        if (session.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        return session;
    }

    /// <summary>Test helper: force-expire a session while leaving it findable as expired on Take/Get.</summary>
    public void ExpireForTests(string importId)
    {
        if (!_sessions.TryGetValue(importId, out var session)) return;
        _sessions[importId] = new ImportSession
        {
            ImportId = session.ImportId,
            ManagerUserId = session.ManagerUserId,
            Entity = session.Entity,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Payload = session.Payload
        };
    }
}
