namespace Shimi.ServiceAbstractions;

/// <summary>
/// TODO
/// </summary>
public interface IStateStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
}
