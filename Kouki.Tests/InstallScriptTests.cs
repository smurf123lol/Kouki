namespace Kouki.Tests;

/// <summary>
/// Проверки установщика (<c>deploy/install.sh</c>) на «переездные» регрессии.
/// </summary>
/// <remarks>
/// После переустановки сервера на NVMe выяснилось, что установщик не закрывал
/// четыре вещи, и каждая всплыла отдельной аварией: таймер бэкапа остался
/// выключенным, SMB-пользователь пропал вместе с <c>passdb.tdb</c>, у агента не
/// было ни git-настроек, ни PAT для push, а бот без <c>NO_PROXY</c> ходил через
/// прокси к собственному серверу и получал 503. Эти тесты держат пункты
/// установщика на месте: их случайное удаление — тихая регрессия, которую
/// заметят только при следующей переустановке.
/// </remarks>
public sealed class InstallScriptTests
{
    [Fact]
    public void Installer_enables_the_backup_timer()
    {
        var text = File.ReadAllText(Path("install.sh"));
        var guards = File.ReadAllText(TestPaths.Path("deploy", "lib", "install-guards.sh"));

        Assert.Contains("kouki-backup.timer", text, StringComparison.Ordinal);
        Assert.Contains("/var/backups/kouki", text, StringComparison.Ordinal);

        // Каталог бэкапов общий для сервиса (kouki) и админа: группа kouki,
        // setgid — как у остальных общих каталогов сервера.
        Assert.Contains("install -d -o root -g \"$USER_NAME\" -m 2775 /var/backups/kouki", text, StringComparison.Ordinal);

        // Решение «включать ли таймер» вынесено в guards: его проверяют
        // поведенческие тесты (InstallGuardsTests), а не поиск подстрок.
        Assert.Contains("apply_backup_timer", text, StringComparison.Ordinal);
        Assert.Contains("systemctl enable --now kouki-backup.timer", guards, StringComparison.Ordinal);
        Assert.Contains("systemctl start kouki-backup.service", guards, StringComparison.Ordinal);
    }

    [Fact]
    public void NoBackup_disables_an_already_enabled_timer()
    {
        var text = File.ReadAllText(Path("install.sh"));

        // Иначе админ, прочитав README, выключит бэкап флагом — и копии продолжат идти.
        // Гасим через guards: юнит бывает и в /lib, и симлинком, а проверка
        // `[ -f /etc/systemd/system/… ]` такие случаи пропускала.
        Assert.Contains("stop_backup_timer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("systemctl disable --now kouki-backup.timer", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_probe_backup_fails_install()
    {
        var text = File.ReadAllText(Path("install.sh"));
        var guards = File.ReadAllText(TestPaths.Path("deploy", "lib", "install-guards.sh"));

        // Провал пробного прогона означает «копий нет»: таймер гасит guards,
        // установку помечаем замечанием и выходим с кодом 1.
        Assert.Contains("if ! apply_backup_timer", text, StringComparison.Ordinal);
        Assert.Contains("PROBLEMS=1", text, StringComparison.Ordinal);
        Assert.Contains("exit 1", text, StringComparison.Ordinal);
        Assert.Contains("пробный бэкап упал", guards, StringComparison.Ordinal);
        Assert.Contains("копий нет", guards, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_git_token_is_rejected()
    {
        var text = File.ReadAllText(Path("install.sh"));

        // Пустой файл токена — ошибка ввода, а не «агент просто без PAT».
        Assert.Contains("Токен GitHub пустой", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Proxy_dropin_is_private_and_escaped()
    {
        var text = File.ReadAllText(Path("install.sh"));

        // В прокси бывают креды: файл юнита не должен быть читаем всем.
        Assert.Contains("chmod 600 \"$PROXY_DIR/proxy.conf\"", text, StringComparison.Ordinal);

        // «%» в systemd — спецификатор: без «%%» юнит не загрузится.
        Assert.Contains("s/\\\\/\\\\\\\\/g", text, StringComparison.Ordinal);
        Assert.Contains("s/%/%%/g", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Samba_user_keeps_the_share_owner_ids()
    {
        var text = File.ReadAllText(Path("install.sh"));
        var guards = File.ReadAllText(TestPaths.Path("deploy", "lib", "install-guards.sh"));

        // После переустановки системы /srv/files помнит числового владельца:
        // новый useradd с другим uid/gid получил бы шару без права записи.
        // Сам разбор uid/gid и группы — в guards, там же и проверка поведения.
        Assert.Contains("resolve_share_ids", text, StringComparison.Ordinal);
        // Создание вынесено в guards: uid/gid передаются только когда известны,
        // иначе `useradd --gid ""` падает «group '' does not exist».
        Assert.Contains("create_share_user", text, StringComparison.Ordinal);
        // `groupadd --gid` — законный вызов (нет группы владельца шары);
        // запрещён именно useradd с непроверенными идентификаторами.
        Assert.DoesNotContain("useradd --uid", text, StringComparison.Ordinal);
        Assert.Contains("stat -c %u \"$dir\"", guards, StringComparison.Ordinal);
        Assert.Contains("groupadd --gid", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_creates_smb_user_with_password()
    {
        var text = File.ReadAllText(Path("install.sh"));

        Assert.Contains("--smb-user", text, StringComparison.Ordinal);
        Assert.Contains("--smb-password", text, StringComparison.Ordinal);

        // smbpasswd в неинтерактивном режиме: пароль читается со stdin дважды.
        Assert.Contains("smbpasswd -s -a", text, StringComparison.Ordinal);

        // Без пароля установка должна падать сразу, а не спрашивать его в tty.
        Assert.Contains("Для --smb-user нужен пароль", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Smb_password_can_come_from_a_file_or_stdin()
    {
        var text = File.ReadAllText(Path("install.sh"));

        // Аргумент виден в ps и в истории shell; файл и stdin — способы без этого.
        Assert.Contains("--smb-password-file", text, StringComparison.Ordinal);
        Assert.Contains("--smb-password-stdin", text, StringComparison.Ordinal);
        Assert.Contains("Пароль SMB передан дважды", text, StringComparison.Ordinal);
        Assert.Contains("Пароль SMB пустой", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_sets_up_git_for_the_agent()
    {
        var text = File.ReadAllText(Path("install.sh"));

        Assert.Contains("git config --global user.name", text, StringComparison.Ordinal);
        Assert.Contains("--add safe.directory", text, StringComparison.Ordinal);

        // PAT кладётся только по явному флагу и с правами 600.
        Assert.Contains("--git-token", text, StringComparison.Ordinal);
        Assert.Contains("--git-token-file", text, StringComparison.Ordinal);
        Assert.Contains("--git-token-stdin", text, StringComparison.Ordinal);
        Assert.Contains("Токен GitHub передан дважды", text, StringComparison.Ordinal);
        Assert.Contains(".git-credentials", text, StringComparison.Ordinal);
        Assert.Contains("chmod 600", text, StringComparison.Ordinal);

        // Креды других хостов не теряются: заменяется только строка github.com.
        Assert.Contains("grep -v 'github\\.com'", text, StringComparison.Ordinal);

        // Помощник хранения — только для github.com: иначе git отдаёт файл
        // любому хосту, к которому обратится агент.
        Assert.Contains("credential.https://github.com.helper", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Proxy_dropin_is_removed_when_flag_is_gone()
    {
        var text = File.ReadAllText(Path("install.sh"));

        // Иначе после отказа от прокси бот продолжит ходить через закрытый прокси.
        Assert.Contains("rm -f \"$PROXY_DIR/proxy.conf\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_uses_the_real_share_account()
    {
        string text = File.ReadAllText(Path("README.md"));

        // Учётка шары в smb.conf — smurf123lol; пример с выдуманным именем
        // создавал пользователя без доступа к /srv/files, а установщик печатал успех.
        Assert.Contains("--smb-user smurf123lol", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--smb-user smurf ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_proxy_for_bot_excludes_local_addresses()
    {
        var text = File.ReadAllText(Path("install.sh"));

        Assert.Contains("--bot-proxy", text, StringComparison.Ordinal);

        // Локальные адреса обязаны идти мимо прокси: иначе бот не достучится
        // до своего же сервера на 127.0.0.1:5300. IPv6-петля — тоже локальный.
        Assert.Contains("no_proxy_unit=\"127.0.0.1,localhost,::1", text, StringComparison.Ordinal);
        Assert.Contains("Environment=\\\"NO_PROXY=$no_proxy_unit\\\"", text, StringComparison.Ordinal);
        Assert.Contains("proxy.conf", text, StringComparison.Ordinal);

        // Обновление артефакта с прокси не должно требовать --with-bot, если
        // бот уже установлен: тогда проверка смотрит на наличие юнита.
        Assert.Contains("[ ! -f \"$BOT_SERVICE\" ]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_folder_has_everything_installer_copies()
    {
        foreach (string name in new[] { "kouki-backup.sh", "kouki-backup.service", "kouki-backup.timer", "backup.env.example" })
        {
            Assert.True(File.Exists(Path(name)), $"в deploy/ нет {name}: установщик не сможет настроить бэкап");
        }

        // Guards подключается через `source` — без файла установщик падает сразу.
        Assert.True(
            File.Exists(TestPaths.Path("deploy", "lib", "install-guards.sh")),
            "в deploy/lib нет install-guards.sh: установщик не запустится");
    }

    [Fact]
    public void Ci_runs_shell_checks()
    {
        string text = File.ReadAllText(TestPaths.Path(".github", "workflows", "ci.yml"));

        // bash -n ловит синтаксис, shellcheck — типовые ошибки в скриптах,
        // которые исполняются от root на сервере.
        Assert.Contains("shellcheck", text, StringComparison.Ordinal);
        Assert.Contains("bash -n", text, StringComparison.Ordinal);
    }

    private static string Path(string name) => TestPaths.Path("deploy", name);
}
