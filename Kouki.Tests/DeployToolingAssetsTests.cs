namespace Kouki.Tests;

/// <summary>
/// Контракт инструментов деплоя для Linux (T85): публикация без PowerShell и
/// код-деплой без установщика. Shell целиком тестами не покрыть, но свойства,
/// которые ломаются при правках, — можно. Именно они уже стоили одного простоя:
/// framework-dependent артефакт поверх self-contained деплоя снёс бы рантайм.
/// </summary>
public sealed class DeployToolingAssetsTests
{
    private static string Script(params string[] parts)
        => File.ReadAllText(TestPaths.Path(parts));

    [Fact]
    public void Publish_builds_all_three_artifacts_and_checks_native_libraries()
    {
        var script = Script("tools", "publish-linux.sh");

        Assert.Contains("Kagedama.api/Kagedama.api.csproj", script, StringComparison.Ordinal);
        Assert.Contains("Kouki.Kagedama.Bot/Kouki.Kagedama.Bot.csproj", script, StringComparison.Ordinal);
        Assert.Contains("Kagedama.mcp/Kagedama.mcp.csproj", script, StringComparison.Ordinal);
        Assert.Contains("KoukiEnableSherpa", script, StringComparison.Ordinal);
        Assert.Contains("libsherpa-onnx-c-api.so", script, StringComparison.Ordinal);
        Assert.Contains("libonnxruntime.so", script, StringComparison.Ordinal);
        Assert.Contains("libe_sqlite3", script, StringComparison.Ordinal);
        Assert.Contains("wwwroot/app.js", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_fails_with_a_readable_message_when_dotnet_is_missing()
    {
        var script = Script("tools", "publish-linux.sh");

        Assert.Contains("нет dotnet в PATH", script, StringComparison.Ordinal);
        Assert.Contains("set -euo pipefail", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_refuses_to_mix_self_contained_and_framework_dependent_shapes()
    {
        var script = Script("deploy", "deploy-code.sh");

        Assert.Contains("libcoreclr.so", script, StringComparison.Ordinal);
        Assert.Contains("форма деплоя не совпадает", script, StringComparison.Ordinal);
        Assert.Contains("--self-contained", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_keeps_bot_and_mcp_out_of_the_api_rsync()
    {
        var script = Script("deploy", "deploy-code.sh");

        // bot/ и mcp/ живут отдельными каталогами; --delete по ним снёс бы сервисы.
        Assert.Contains("--exclude 'bot/'", script, StringComparison.Ordinal);
        Assert.Contains("--exclude 'mcp/'", script, StringComparison.Ordinal);
        Assert.Contains("--exclude 'appsettings.Development.json'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_passes_the_api_token_through_the_environment_not_argv()
    {
        var script = Script("deploy", "deploy-code.sh");

        // Смоуку токен нужен в окружении: в argv его увидел бы ps.
        Assert.Contains("KOUKI_TOKEN=\"$TOKEN\" bash", script, StringComparison.Ordinal);
        Assert.DoesNotContain("smoke.sh\" --token", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_has_a_dry_run_that_changes_nothing()
    {
        var script = Script("deploy", "deploy-code.sh");

        Assert.Contains("dry-run: ничего не изменено", script, StringComparison.Ordinal);
        Assert.Contains("--dry-run", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_tooling_scripts_answer_help_and_reject_unknown_arguments()
    {
        foreach (var path in new[] { new[] { "tools", "publish-linux.sh" }, new[] { "deploy", "deploy-code.sh" } })
        {
            var script = Script(path);

            Assert.Contains("usage()", script, StringComparison.Ordinal);
            Assert.Contains("--help", script, StringComparison.Ordinal);
            Assert.Contains("неизвестный аргумент", script, StringComparison.Ordinal);

            var head = File.ReadAllBytes(TestPaths.Path(path)).Take(2).ToArray();
            Assert.Equal((byte)'#', head[0]);
            Assert.Equal((byte)'!', head[1]);
        }
    }
}
