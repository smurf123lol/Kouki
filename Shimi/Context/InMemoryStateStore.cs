using Shimi.ServiceAbstractions;

namespace Shimi.Context;

public class InMemoryStateStore : IStateStore
{
    private readonly Dictionary<string, object> _store = new();

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        _store.TryGetValue(key, out var value);
        return Task.FromResult(value is T t ? t : default);
    }

    public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        _store[key] = value!;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _store.Remove(key);
        return Task.CompletedTask;
    }
}