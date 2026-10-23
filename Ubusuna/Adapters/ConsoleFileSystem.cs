using Kouki.Shimi.ServiceAbstractions;
using System.Text;
using System.Text.Json;

namespace Kouki.Ubusuna.Adapters;

/// <summary>
/// Реализация <see cref="IFileSystem"/> поверх локальной файловой системы,
/// ограниченная корневым каталогом.
/// </summary>
/// <remarks>
/// Любой путь сначала нормализуется относительно <c>_rootPath</c> и затем проверяется:
/// выход за пределы корня (в том числе через <c>..</c> или абсолютный путь) запрещён.
/// </remarks>
public class ConsoleFileSystem : IFileSystem, ITextFileSystem, IJsonFileSystem
{
    private readonly string _rootPath;

    public ConsoleFileSystem(string? rootPath = null)
    {
        _rootPath = Path.GetFullPath(rootPath ?? Directory.GetCurrentDirectory());
    }

    /// <summary>Корневой каталог, за пределы которого выходить нельзя.</summary>
    public string RootPath => _rootPath;

    private string GetFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Путь не может быть пустым.", nameof(path));

        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, path));
        var relative = Path.GetRelativePath(_rootPath, fullPath);

        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new UnauthorizedAccessException(
                $"Путь '{path}' выходит за пределы корневого каталога '{_rootPath}'.");
        }

        return fullPath;
    }

    // IFileSystem
    public Task<byte[]> ReadBytesAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(File.ReadAllBytes(GetFullPath(path)));
    }

    public Task WriteBytesAsync(string path, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ct.ThrowIfCancellationRequested();

        var fullPath = GetFullPath(path);
        EnsureDirectory(fullPath);
        File.WriteAllBytes(fullPath, data);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var fullPath = GetFullPath(path);
        return Task.FromResult(File.Exists(fullPath) || Directory.Exists(fullPath));
    }

    public Task<string> GetTreeAsync(string path, bool showFiles, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var fullPath = GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {fullPath}");

        return Task.FromResult(BuildTree(fullPath, "", showFiles, ct));
    }

    private static string BuildTree(string path, string indent, bool showFiles, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var builder = new StringBuilder();
        var directories = Directory.GetDirectories(path);
        var files = showFiles ? Directory.GetFiles(path) : Array.Empty<string>();

        foreach (var directory in directories)
        {
            builder.AppendLine($"{indent}├── {Path.GetFileName(directory)}/");
            builder.Append(BuildTree(directory, indent + "│   ", showFiles, ct));
        }

        for (var i = 0; i < files.Length; i++)
        {
            var prefix = i == files.Length - 1 ? "└── " : "├── ";
            builder.AppendLine($"{indent}{prefix}{Path.GetFileName(files[i])}");
        }

        return builder.ToString();
    }

    // ITextFileSystem
    public Task<string> ReadTextAsync(string path, Encoding? encoding = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(File.ReadAllText(GetFullPath(path), encoding ?? Encoding.UTF8));
    }

    public Task WriteTextAsync(string path, string content, Encoding? encoding = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ct.ThrowIfCancellationRequested();

        var fullPath = GetFullPath(path);
        EnsureDirectory(fullPath);
        File.WriteAllText(fullPath, content, encoding ?? Encoding.UTF8);
        return Task.CompletedTask;
    }

    public Task AppendTextAsync(string path, string content, Encoding? encoding = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ct.ThrowIfCancellationRequested();

        var fullPath = GetFullPath(path);
        EnsureDirectory(fullPath);
        File.AppendAllText(fullPath, content, encoding ?? Encoding.UTF8);
        return Task.CompletedTask;
    }

    /// <summary>Создаёт каталог назначения, чтобы запись в новый подкаталог не падала.</summary>
    private static void EnsureDirectory(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    // IJsonFileSystem
    public async Task<T> ReadJsonAsync<T>(string path, JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        var json = await ReadTextAsync(path, ct: ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(json, options)
            ?? throw new InvalidOperationException($"Файл '{path}' содержит null вместо ожидаемого объекта.");
    }

    public async Task WriteJsonAsync<T>(string path, T data, JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(data, options);
        await WriteTextAsync(path, json, ct: ct).ConfigureAwait(false);
    }
}
