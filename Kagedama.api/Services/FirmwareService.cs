using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Kouki.Kagedama.Api.Api;

namespace Kouki.Kagedama.Api.Services;

/// <summary>
/// Результат проверки наличия обновления прошивки станции.
/// </summary>
public enum FirmwareCheckStatus
{
    /// <summary>Каталог не настроен.</summary>
    NotConfigured,

    /// <summary>Каталог пуст или образ не найден.</summary>
    NotFound,

    /// <summary>Образ найден.</summary>
    Success,
}

/// <summary>
/// Сервис управления и отдачи образов прошивки станции для OTA.
/// </summary>
public static class FirmwareService
{
    private const string FilePrefix = "kouki-station-";
    private const string FileSuffix = ".bin";

    private static readonly ConcurrentDictionary<(string Path, DateTime LastWriteUtc), (string Sha256, long Size)> Cache = new();

    /// <summary>
    /// Проверяет наличие новой версии прошивки для станции.
    /// </summary>
    public static (FirmwareCheckStatus Status, FirmwareInfoResponse? Info) GetLatestFirmware(
        string? currentVersion,
        KoukiServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.FirmwareDirectory))
        {
            return (FirmwareCheckStatus.NotConfigured, null);
        }

        string dir = ResolveDirectory(options.FirmwareDirectory, options.RequireAbsolutePaths);
        if (!Directory.Exists(dir))
        {
            return (FirmwareCheckStatus.NotFound, null);
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(dir, $"{FilePrefix}*{FileSuffix}", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return (FirmwareCheckStatus.NotFound, null);
        }

        if (files.Length == 0)
        {
            return (FirmwareCheckStatus.NotFound, null);
        }

        (StationSemver Semver, string RawVersion, string Path)? latest = null;

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            if (!name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string verStr = name[FilePrefix.Length..^FileSuffix.Length];
            if (StationSemver.TryParse(verStr, out StationSemver semver))
            {
                if (latest is null || semver > latest.Value.Semver)
                {
                    latest = (semver, verStr, file);
                }
            }
        }

        if (latest is null)
        {
            return (FirmwareCheckStatus.NotFound, null);
        }

        (string sha256, long size) = GetFileInfo(latest.Value.Path);

        bool isUpdate = true;
        if (StationSemver.TryParse(currentVersion, out StationSemver curVer))
        {
            isUpdate = latest.Value.Semver > curVer;
        }

        string url = $"/api/firmware/image?version={latest.Value.RawVersion}";
        var response = new FirmwareInfoResponse(latest.Value.RawVersion, sha256, size, url, isUpdate);

        return (FirmwareCheckStatus.Success, response);
    }

    /// <summary>
    /// Открывает файл образа прошивки заданной версии.
    /// </summary>
    public static (FirmwareCheckStatus Status, FileStream? Stream, string? Sha256, long Size) TryOpenFile(
        string? version,
        KoukiServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.FirmwareDirectory))
        {
            return (FirmwareCheckStatus.NotConfigured, null, null, 0);
        }

        if (string.IsNullOrWhiteSpace(version) ||
            version.Contains("..", StringComparison.Ordinal) ||
            version.Contains('/', StringComparison.Ordinal) ||
            version.Contains('\\', StringComparison.Ordinal))
        {
            return (FirmwareCheckStatus.NotFound, null, null, 0);
        }

        string dir = ResolveDirectory(options.FirmwareDirectory, options.RequireAbsolutePaths);
        string fileName = $"{FilePrefix}{version}{FileSuffix}";
        string filePath = Path.Combine(dir, fileName);

        string fullDir = Path.GetFullPath(dir);
        string fullPath = Path.GetFullPath(filePath);

        if (!fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase))
        {
            return (FirmwareCheckStatus.NotFound, null, null, 0);
        }

        FileStream stream;
        try
        {
            stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (FirmwareCheckStatus.NotFound, null, null, 0);
        }

        (string sha256, long size) = GetFileInfo(fullPath);
        return (FirmwareCheckStatus.Success, stream, sha256, size);
    }

    private static (string Sha256, long Size) GetFileInfo(string filePath)
    {
        DateTime lastWrite = File.GetLastWriteTimeUtc(filePath);
        var key = (filePath, lastWrite);

        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = SHA256.HashData(fs);
        string sha256 = Convert.ToHexStringLower(hash);
        long size = fs.Length;

        var result = (sha256, size);
        Cache[key] = result;
        return result;
    }

    private static string ResolveDirectory(string path, bool requireAbsolute)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        if (requireAbsolute)
        {
            throw new InvalidOperationException($"Путь к каталогу прошивок '{path}' должен быть абсолютным (Kouki:RequireAbsolutePaths).");
        }

        return Path.Combine(AppContext.BaseDirectory, path);
    }
}
