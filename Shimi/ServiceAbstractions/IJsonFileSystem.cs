using System.Text.Json;

namespace Shimi.ServiceAbstractions;

/// <summary>
/// Расширение для работы с JSON-файлами.
/// </summary>

public interface IJsonFileSystem : IFileSystem
{
    Task<T> ReadJsonAsync<T>(string path, JsonSerializerOptions? options = null, CancellationToken ct = default);
    Task WriteJsonAsync<T>(string path, T data, JsonSerializerOptions? options = null, CancellationToken ct = default);
}