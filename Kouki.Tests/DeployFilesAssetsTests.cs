using System.Text.RegularExpressions;

namespace Kouki.Tests;

/// <summary>
/// Проверки артефактов сетевого диска (<c>deploy/files/</c>): конфиг Samba,
/// экспорт NFS и инструкция должны описывать то же, что реально настроено
/// на сервере.
/// </summary>
/// <remarks>
/// Тесты смотрят на смысл строк, а не на форматирование: конфиг можно
/// выровнять иначе, но SMB1 обязан остаться выключенным, гостевой доступ —
/// запрещённым, а экспорт — ограниченным подсетью. Ошибки здесь дорогие: это
/// единственные файлы, по которым на сервере открывается каталог на 7 ТБ.
/// </remarks>
public sealed class DeployFilesAssetsTests
{
    [Fact]
    public void Samba_disables_guest_and_ancient_protocols()
    {
        var text = Read("smb.conf.example", "конфиг Samba");

        Assert.Matches(new Regex(@"server min protocol\s*=\s*SMB[23]", RegexOptions.Multiline), text);
        Assert.DoesNotMatch(new Regex(@"server min protocol\s*=\s*(NT1|SMB1|CORE|LANMAN)", RegexOptions.Multiline), text);

        Assert.Matches(new Regex(@"map to guest\s*=\s*never", RegexOptions.Multiline), text);
        Assert.DoesNotMatch(new Regex(@"^\s*guest ok\s*=\s*yes", RegexOptions.Multiline), text);

        // Печать серверу файлов не нужна: лишний демон — лишние CVE.
        Assert.Matches(new Regex(@"load printers\s*=\s*no", RegexOptions.Multiline), text);
        Assert.Matches(new Regex(@"disable spoolss\s*=\s*yes", RegexOptions.Multiline), text);
    }

    [Fact]
    public void Samba_share_is_writable_but_not_anonymous()
    {
        var text = Read("smb.conf.example", "конфиг Samba");

        Assert.Matches(new Regex(@"path\s*=\s*/srv/files", RegexOptions.Multiline), text);
        Assert.Matches(new Regex(@"read only\s*=\s*no", RegexOptions.Multiline), text);

        // valid users обязателен: без него шара открыта всякому, кто прошёл
        // аутентификацию любым способом.
        var valid = Regex.Match(text, @"valid users\s*=\s*(?<users>.+)");
        Assert.True(valid.Success, "нет valid users — доступ к шаре не ограничен поимённо");
        Assert.DoesNotContain("@", valid.Groups["users"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Nfs_export_is_limited_to_a_private_subnet_and_writable()
    {
        var text = Read("exports.example", "экспорт NFS");

        var line = Regex.Match(
            text,
            @"^/srv/files\s+(?<host>[0-9.]+/\d+)(?<options>\([^)]*\))?",
            RegexOptions.Multiline);
        Assert.True(line.Success, "в примере /etc/exports нет строки для /srv/files");

        // "*" или пустой хост означают «открыто всему интернету».
        var host = line.Groups["host"].Value;
        Assert.DoesNotContain("*", host, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+\.\d+/\d+$"), host);

        var options = line.Groups["options"].Value;
        Assert.Contains("rw", options, StringComparison.Ordinal);
        Assert.Contains("sync", options, StringComparison.Ordinal);
    }

    [Fact]
    public void Samba_does_not_fight_for_browse_master_and_does_not_bind_one_interface()
    {
        var text = Read("smb.conf.example", "конфиг Samba");

        // `os level` и участие в выборах браузера к доступу к шаре отношения не
        // имеют, зато ломают список «Сеть» в проводнике. У Samba по умолчанию
        // os level = 20 — «обычный участник», этого достаточно.
        Assert.DoesNotMatch(new Regex(@"^\s*os level\s*=", RegexOptions.Multiline), text);
        Assert.Matches(new Regex(@"^\s*local master\s*=\s*no", RegexOptions.Multiline), text);
        Assert.Matches(new Regex(@"^\s*domain master\s*=\s*no", RegexOptions.Multiline), text);

        // Никакой привязки к интерфейсу: живой канал — USB-Wi-Fi с именем
        // `wlx<mac>`, оно меняется при переподключении адаптера, и привязка
        // молча оставила бы SMB недоступным. Комментарии со строкой не в счёт.
        Assert.DoesNotMatch(new Regex(@"^\s*interfaces\s*=", RegexOptions.Multiline), text);
        Assert.DoesNotMatch(new Regex(@"^\s*bind interfaces only\s*=", RegexOptions.Multiline), text);
    }

    [Fact]
    public void Readme_lists_every_way_into_the_same_folder()
    {
        var text = Read("README.md", "README сетевого диска");

        Assert.Contains(@"\\<ip-сервера>\files", text, StringComparison.Ordinal);
        Assert.Contains("smb://", text, StringComparison.Ordinal);
        Assert.Contains("mount -t nfs", text, StringComparison.Ordinal);

        // Две самые частые путаницы: пароль SMB отдельный от системного и
        // root_squash, который принимают за ошибку.
        Assert.Contains("smbpasswd", text, StringComparison.Ordinal);
        Assert.Contains("root_squash", text, StringComparison.Ordinal);

        // Решение «не привязываемся к интерфейсу» должно быть объяснено, иначе
        // следующий читатель «починит» его обратно.
        Assert.Contains("wlx", text, StringComparison.Ordinal);
        Assert.Contains("bind interfaces only", text, StringComparison.Ordinal);
    }

    private static string Read(string name, string what)
    {
        string path = TestPaths.Path("deploy", "files", name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"нет {what}: {path}");
        }

        return File.ReadAllText(path);
    }
}
