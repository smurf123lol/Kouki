using System.Text;
namespace Shimi.ServiceAbstractions;

public interface ITextFileSystem : IFileSystem
{
    Task<string> ReadTextAsync(string path, Encoding? encoding = null, CancellationToken ct = default);
    Task WriteTextAsync(string path, string content, Encoding? encoding = null, CancellationToken ct = default);
    Task AppendTextAsync(string path, string content, Encoding? encoding = null, CancellationToken ct = default);
}