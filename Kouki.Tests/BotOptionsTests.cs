using Kouki.Kagedama.Bot;

namespace Kouki.Tests;

public sealed class BotOptionsTests : IDisposable
{
    private readonly string _root;

    public BotOptionsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kouki-bot-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // временный каталог, уборка не критична
        }
    }

    [Fact]
    public void Reads_settings_from_appsettings_json()
    {
        WriteSettings("""
        {
          "KoukiBot": {
            "BotToken": "123:ABC",
            "ServerUrl": "http://127.0.0.1:5300",
            "Source": "telegram",
            "PollTimeoutSeconds": 15
          }
        }
        """);

        BotOptions options = BotOptions.Load([], _root);

        Assert.Equal("123:ABC", options.BotToken);
        Assert.Equal("http://127.0.0.1:5300", options.ServerUrl);
        Assert.Equal(15, options.PollTimeoutSeconds);
    }

    [Fact]
    public void Missing_file_falls_back_to_defaults()
    {
        BotOptions options = BotOptions.Load([], Path.Combine(_root, "нет-файла"));

        Assert.Equal(string.Empty, options.BotToken);
        Assert.Equal("https://api.telegram.org", options.TelegramApiUrl);
        Assert.Equal(25, options.PollTimeoutSeconds);
    }

    [Fact]
    public void Broken_json_is_reported_instead_of_silently_ignored()
    {
        WriteSettings("{ \"KoukiBot\": { \"BotToken\": } ");

        BotConfigurationException ex = Assert.Throws<BotConfigurationException>(
            () => BotOptions.Load([], _root));

        Assert.Contains("appsettings.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("KOUKI_BOT_TOKEN", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Arguments_overpower_file()
    {
        WriteSettings("""
        {
          "KoukiBot": { "BotToken": "from-file", "ServerUrl": "http://from-file" }
        }
        """);

        BotOptions options = BotOptions.Load(
            ["--bot-token=from-args", "--server-url=http://from-args", "--poll-timeout=7"],
            _root);

        Assert.Equal("from-args", options.BotToken);
        Assert.Equal("http://from-args", options.ServerUrl);
        Assert.Equal(7, options.PollTimeoutSeconds);
    }

    private void WriteSettings(string json)
        => File.WriteAllText(Path.Combine(_root, "appsettings.json"), json);
}
