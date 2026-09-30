namespace Shimi.ServiceAbstractions;

/// <summary>
/// TODO
/// </summary>
public interface IFileSystem
{
    Task<byte[]> ReadBytesAsync(string path, CancellationToken ct = default);
    Task WriteBytesAsync(string path, byte[] data, CancellationToken ct = default);
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);
    Task<string> GetTreeAsync(string path, bool showFiles, CancellationToken ct = default);
}
