using Kouki.Kagedama.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kouki.Tests;

public class KoukiPathsTests
{
    [Fact]
    public void Absolute_path_is_left_as_is()
    {
        string absolute = Path.Combine(AppContext.BaseDirectory, "data", "kouki.sqlite");

        string resolved = KoukiPaths.Resolve(absolute, "kouki.sqlite", "DatabasePath");

        Assert.Equal(Path.GetFullPath(absolute), resolved);
        Assert.True(Path.IsPathRooted(resolved));
    }

    [Fact]
    public void Empty_value_uses_fallback_next_to_application()
    {
        string resolved = KoukiPaths.Resolve("   ", "kouki.sqlite", "DatabasePath");

        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "kouki.sqlite")), resolved);
    }

    [Fact]
    public void Relative_path_is_resolved_from_application_directory_not_current_directory()
    {
        string resolved = KoukiPaths.Resolve("audio", "audio", "AudioDirectory");

        Assert.True(Path.IsPathRooted(resolved));
        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "audio")), resolved);
    }

    [Fact]
    public void RequireAbsolute_rejects_relative_path_with_actionable_message()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => KoukiPaths.Resolve("audio", "audio", "AudioDirectory", requireAbsolute: true));

        Assert.Contains("AudioDirectory", error.Message);
        Assert.Contains("абсолютным", error.Message);
        Assert.Contains("Kouki__AudioDirectory", error.Message);
    }

    [Fact]
    public void RequireAbsolute_accepts_absolute_path()
    {
        string absolute = Path.Combine(AppContext.BaseDirectory, "var", "audio");

        string resolved = KoukiPaths.Resolve(absolute, "audio", "AudioDirectory", requireAbsolute: true);

        Assert.Equal(Path.GetFullPath(absolute), resolved);
    }

    [Fact]
    public void Logger_is_optional_and_message_mentions_setting()
    {
        string resolved = KoukiPaths.Resolve("models", "models", "SpeechModelDirectory", logger: NullLogger.Instance);

        Assert.True(Path.IsPathRooted(resolved));
    }
}
