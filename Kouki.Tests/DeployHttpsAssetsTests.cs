namespace Kouki.Tests;

/// <summary>
/// Контракт https-прокси (T55). Проверяется текст файлов деплоя: сам Caddy мы
/// здесь не запускаем, но обязательные свойства конфигурации — «внутренний CA
/// по умолчанию», «апстрим только loopback», «переключение режима без правки
/// файлов» — ловятся текстом.
/// </summary>
public sealed class DeployHttpsAssetsTests
{
    private static string DeployFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "deploy", name);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Не нашёл deploy/{name} выше {AppContext.BaseDirectory}");
    }

    [Fact]
    public void Internal_caddyfile_uses_local_ca_and_loopback_upstream()
    {
        var config = DeployFile("Caddyfile.internal");

        // По умолчанию — локальный CA: интернета на стенде может не быть.
        Assert.Contains("tls internal", config, StringComparison.Ordinal);
        // Приложение не должно быть доступно в обход прокси: апстрим — loopback,
        // а не 0.0.0.0. Подстановка значения — забота установщика, поэтому
        // проверяем «прокси на loopback», а не конкретную строку с переменной.
        Assert.Contains("reverse_proxy", config, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:5300", config, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", config, StringComparison.Ordinal);
        // http на 80 — только редирект, иначе токен уезжает открытым текстом.
        Assert.Contains("redir https://", config, StringComparison.Ordinal);
        Assert.Contains("encode", config, StringComparison.Ordinal);
    }

    [Fact]
    public void Acme_caddyfile_takes_domain_and_email_from_environment()
    {
        var config = DeployFile("Caddyfile.acme");

        Assert.Contains("{$KOUKI_DOMAIN}", config, StringComparison.Ordinal);
        Assert.Contains("{$KOUKI_ACME_EMAIL}", config, StringComparison.Ordinal);
        Assert.Contains("reverse_proxy", config, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:5300", config, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", config, StringComparison.Ordinal);
        Assert.DoesNotContain("tls internal", config, StringComparison.Ordinal);
    }

    [Fact]
    public void Caddy_env_example_defaults_to_internal_mode()
    {
        var env = DeployFile("caddy.env.example");

        Assert.Contains("KOUKI_HTTPS_MODE=internal", env, StringComparison.Ordinal);
        Assert.Contains("KOUKI_UPSTREAM=127.0.0.1:5300", env, StringComparison.Ordinal);
        Assert.Contains("KOUKI_DOMAIN=", env, StringComparison.Ordinal);
        Assert.Contains("KOUKI_ACME_EMAIL=", env, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_can_enable_caddy_and_switch_mode()
    {
        var script = DeployFile("install.sh");

        Assert.Contains("--with-caddy", script, StringComparison.Ordinal);
        Assert.Contains("--https-mode", script, StringComparison.Ordinal);
        Assert.Contains("--domain", script, StringComparison.Ordinal);
        Assert.Contains("--acme-email", script, StringComparison.Ordinal);
        Assert.Contains("Caddyfile.internal", script, StringComparison.Ordinal);
        Assert.Contains("Caddyfile.acme", script, StringComparison.Ordinal);
        // Конфиг проверяется до перезапуска — иначе сервис молча не поднимется.
        Assert.Contains("caddy validate", script, StringComparison.Ordinal);
        Assert.Contains("systemctl enable --now caddy", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_env_example_binds_loopback_only_with_caddy()
    {
        var env = DeployFile("kouki.env.example");

        Assert.Contains("ASPNETCORE_URLS=http://127.0.0.1:5300", env, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_explains_trusting_local_ca_and_keeps_station_on_http()
    {
        var readme = DeployFile("README.md");

        Assert.Contains("tls internal", readme, StringComparison.Ordinal);
        Assert.Contains("root.crt", readme, StringComparison.Ordinal);
        Assert.Contains("caddy trust", readme, StringComparison.Ordinal);
        // Станция пока остаётся на http внутри LAN — это осознанное решение, а не забывчивость.
        Assert.Contains("станция", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KOUKI_HTTPS_MODE", readme, StringComparison.Ordinal);
    }
}
