using System.Collections.Concurrent;

namespace MbaLms.Api.Infrastructure.Email;

public sealed class MemoryEmailOutbox : IEmailOutbox
{
    private readonly ConcurrentDictionary<Guid, CapturedEmailMessage> _items = new();

    public Guid Add(CapturedEmailMessage message)
    {
        _items[message.Id] = message;
        return message.Id;
    }

    public CapturedEmailMessage? Get(Guid id) =>
        _items.TryGetValue(id, out var m) ? m : null;

    public CapturedEmailMessage? LatestFor(string to) =>
        _items.Values
            .Where(m => string.Equals(m.To, to, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.CapturedAt)
            .FirstOrDefault();

    public void Clear() => _items.Clear();
}
