using System.Text.RegularExpressions;

namespace Kouki.Tests;

/// <summary>
/// Проверки юнитов systemd из <c>deploy/</c>: ключ, положенный не в свою секцию,
/// systemd молча игнорирует — сервис работает, но не так, как записано в файле.
/// </summary>
/// <remarks>
/// Так уже случалось дважды: <c>StartLimitIntervalSec</c> в <c>[Service]</c> у сервера
/// и у бота. Журнал ругался «Unknown key», а предел перезапусков не действовал.
/// </remarks>
public sealed class DeployUnitsAssetsTests
{
    [Fact]
    public void Bot_unit_keeps_start_limits_in_unit_section()
    {
        string path = TestPaths.Path("deploy", "kouki-bot.service");
        Assert.True(File.Exists(path), "нет юнита бота: " + path);
        string text = File.ReadAllText(path);

        string unit = Section(text, "Unit");
        string service = Section(text, "Service");

        Assert.Contains("StartLimitIntervalSec", unit, StringComparison.Ordinal);
        Assert.Contains("StartLimitBurst", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("StartLimitIntervalSec", service, StringComparison.Ordinal);
        Assert.DoesNotContain("StartLimitBurst", service, StringComparison.Ordinal);

        // Старт бота не должен ждать network-online: на этой машине сеть поднимается
        // позже юнитов, и ожидание съедало две минуты загрузки.
        Assert.DoesNotContain("network-online.target", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_script_keeps_bot_and_mcp_directories()
    {
        // Сервер и бот публикуются отдельно. `rsync --delete` по /opt/kouki/app
        // без исключений стирал `bot/` — бот падал на «Changing to the requested
        // working directory failed» и молча не отвечал в Telegram.
        string path = TestPaths.Path("deploy", "install.sh");
        Assert.True(File.Exists(path), "нет install.sh: " + path);
        string text = File.ReadAllText(path);

        Assert.Contains("--exclude 'bot/'", text, StringComparison.Ordinal);
        Assert.Contains("--exclude 'mcp/'", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Bot_unit_starts_with_the_server()
    {
        // PartOf останавливает бота вместе с сервером, но не поднимает его обратно.
        // Без Upholds остановка сервера (любой деплой) оставляла бота лежать.
        string text = File.ReadAllText(TestPaths.Path("deploy", "kouki-bot.service"));

        Assert.Contains("Upholds=kouki.service", Section(text, "Unit"), StringComparison.Ordinal);
    }
    private static string Section(string text, string name)
    {
        Match match = Regex.Match(
            text,
            $@"^\[{name}\](?<body>.*?)(^\[|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline);

        Assert.True(match.Success, "в юните нет секции [" + name + "]");
        return match.Groups["body"].Value;
    }
}
