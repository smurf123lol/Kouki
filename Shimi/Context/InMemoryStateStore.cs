using System.Collections.Concurrent;
using Kouki.Shimi.ServiceAbstractions;

namespace Kouki.Shimi.Context;

/// <summary>
/// Потокобезопасное хранилище состояния в памяти процесса.
/// </summary>
public sealed class InMemoryStateStore : IStateStore
{
    private readonly ConcurrentDictionary<string, object?> _store =
        new(StringComparer.Ordinal);

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _store.TryGetValue(key, out var value);
        return Task.FromResult(value is T typed ? typed : default);
    }

    public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
