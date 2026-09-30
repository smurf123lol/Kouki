// Kouki.Ubusuna/Adapters/ConsoleFileSystem.cs
using Shimi.ServiceAbstractions;
using System.Text;
using System.Text.Json;

namespace Kouki.Ubusuna.Adapters;

/// <summary>
/// Реализация IFileSystem для локальной файловой системы.
/// </summary>
public class ConsoleFileSystem : IFileSystem, ITextFileSystem, IJsonFileSystem
{
    private readonly string _rootPath;

    public ConsoleFileSystem(string? rootPath = null)
    {
        _rootPath = rootPath ?? Directory.GetCurrentDirectory();
    }

    private string GetFullPath(string path) => Path.Combine(_rootPath, path);

    // IFileSystem
    public Task<byte[]> ReadBytesAsync(string path, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        return Task.FromResult(File.ReadAllBytes(fullPath));
    }

    public Task WriteBytesAsync(string path, byte[] data, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        File.WriteAllBytes(fullPath, data);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        return Task.FromResult(File.Exists(fullPath) || Directory.Exists(fullPath));
    }

    public Task<string> GetTreeAsync(string path, bool showFiles, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {fullPath}");

        var tree = BuildTree(fullPath, "", showFiles);
        return Task.FromResult(tree);
    }

    private string BuildTree(string path, string indent, bool showFiles)
    {
        var sb = new StringBuilder();
        var dirs = Directory.GetDirectories(path);
        var files = showFiles ? Directory.GetFiles(path) : Array.Empty<string>();

        foreach (var dir in dirs)
        {
            sb.AppendLine($"{indent}├── {Path.GetFileName(dir)}/");
            sb.Append(BuildTree(dir, indent + "│   ", showFiles));
        }

        for (int i = 0; i < files.Length; i++)
        {
            var prefix = i == files.Length - 1 ? "└── " : "├── ";
            sb.AppendLine($"{indent}{prefix}{Path.GetFileName(files[i])}");
        }

        return sb.ToString();
    }

    // ITextFileSystem
    public Task<string> ReadTextAsync(string path, Encoding? encoding = null, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        var enc = encoding ?? Encoding.UTF8;
        return Task.FromResult(File.ReadAllText(fullPath, enc));
    }

    public Task WriteTextAsync(string path, string content, Encoding? encoding = null, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        var enc = encoding ?? Encoding.UTF8;
        File.WriteAllText(fullPath, content, enc);
        return Task.CompletedTask;
    }

    public Task AppendTextAsync(string path, string content, Encoding? encoding = null, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        var enc = encoding ?? Encoding.UTF8;
        File.AppendAllText(fullPath, content, enc);
        return Task.CompletedTask;
    }

    // IJsonFileSystem
    public Task<T> ReadJsonAsync<T>(string path, JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        var json = File.ReadAllText(fullPath);
        var result = JsonSerializer.Deserialize<T>(json, options);
        return Task.FromResult(result!);
    }

    public Task WriteJsonAsync<T>(string path, T data, JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        var json = JsonSerializer.Serialize(data, options);
        File.WriteAllText(fullPath, json);
        return Task.CompletedTask;
    }
}
