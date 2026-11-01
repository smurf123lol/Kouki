using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Kouki.Tests;

/// <summary>
/// Поведенческие проверки ветвей установщика, которые решают «работает ли
/// обещанное»: включение таймера бэкапа и разбор владельца сетевой шары.
/// </summary>
/// <remarks>
/// Строковые тесты (<see cref="InstallScriptTests"/>) ловят удаление кода, но не
/// его логику: ветка «нет sqlite3 — таймер всё равно включается» проходит любую
/// проверку на наличие подстроки. Здесь функции из
/// <c>deploy/lib/install-guards.sh</c> вызываются в настоящем bash с подменёнными
/// <c>systemctl</c>/<c>stat</c>/<c>getent</c>, и тест смотрит, какие команды
/// действительно были вызваны и что вернула функция.
///
/// Без bash (Windows без Git Bash) тесты падают с явным сообщением, а не
/// «пропускаются»: молчаливый пропуск оставил бы ветви установщика без проверки.
/// </remarks>
public sealed class InstallGuardsTests
{
    [Fact]
    public void Missing_tools_are_named()
    {
        Dictionary<string, string> env = new() { ["TOOLS_PRESENT"] = "0" };

        GuardResult result = Run(
            """
            backup_missing_tools
            echo "rc=0"
            """,
            env);

        Assert.Equal("sqlite3 rsync", result.Stdout.Trim());
    }

    [Fact]
    public void Timer_is_switched_on_after_a_successful_probe()
    {
        Dictionary<string, string> env = new()
        {
            ["ENABLED"] = "0",
            ["ACTIVE"] = "1",
            ["START_RC"] = "0",
        };

        GuardResult result = Run(
            """
            apply_backup_timer ""
            echo "rc=$?"
            """,
            env);

        Assert.Equal(0, result.FunctionRc);
        Assert.Contains("systemctl enable --now kouki-backup.timer", result.Calls);
        Assert.Contains("systemctl start kouki-backup.service", result.Calls);
        Assert.DoesNotContain("systemctl disable", result.Calls);
    }

    [Fact]
    public void Failed_probe_switches_the_timer_off_and_reports_failure()
    {
        Dictionary<string, string> env = new()
        {
            ["ENABLED"] = "0",
            ["ACTIVE"] = "1",
            ["START_RC"] = "1",
        };

        GuardResult result = Run(
            """
            apply_backup_timer ""
            echo "rc=$?"
            """,
            env);

        Assert.Equal(1, result.FunctionRc);
        Assert.Contains("systemctl enable --now kouki-backup.timer", result.Calls);
        Assert.Contains("systemctl disable --now kouki-backup.timer", result.Calls);
    }

    [Fact]
    public void Failed_probe_does_not_claim_the_timer_is_off_when_disable_fails()
    {
        // Если disable упал, таймер продолжит падать в 03:30 — обещать
        // «выключен» в этом случае нельзя.
        Dictionary<string, string> env = new()
        {
            ["ENABLED"] = "0",
            ["ACTIVE"] = "1",
            ["START_RC"] = "1",
            ["DISABLE_RC"] = "1",
        };

        GuardResult result = Run(
            """
            apply_backup_timer ""
            echo "rc=$?"
            """,
            env);

        Assert.Equal(1, result.FunctionRc);
        string output = result.Stdout + result.Stderr;
        Assert.Contains("выключить таймер не удалось", output);
        Assert.DoesNotContain("таймер выключен", output);
    }

    [Fact]
    public void Timer_is_not_left_enabled_when_it_never_becomes_active()
    {
        // enable прошёл, is-active — нет: enabled-но-мёртвый таймер тоже врёт,
        // поэтому его надо погасить.
        Dictionary<string, string> env = new()
        {
            ["ENABLED"] = "1",
            ["ACTIVE"] = "0",
            ["ENABLE_RC"] = "0",
        };

        GuardResult result = Run(
            """
            apply_backup_timer ""
            echo "rc=$?"
            """,
            env);

        Assert.Equal(1, result.FunctionRc);
        Assert.Contains("systemctl disable --now kouki-backup.timer", result.Calls);
    }

    [Fact]
    public void Timer_left_from_a_previous_install_is_switched_off_when_tools_are_gone()
    {
        // Регрессия, которую нашёл ревьюер: sqlite3 удалили, таймер от прошлой
        // установки остался active, а установщик писал «не включён», ничего не гася.
        Dictionary<string, string> env = new()
        {
            ["ENABLED"] = "1",
            ["ACTIVE"] = "1",
            ["DISABLE_RC"] = "0",
        };

        GuardResult result = Run(
            """
            apply_backup_timer "sqlite3"
            echo "rc=$?"
            """,
            env);

        Assert.Equal(1, result.FunctionRc);
        Assert.Contains("systemctl disable --now kouki-backup.timer", result.Calls);
        Assert.Contains("нет sqlite3", result.Stdout + result.Stderr);
        Assert.Contains("таймер выключен", result.Stdout + result.Stderr);
        Assert.DoesNotContain("systemctl enable --now", result.Calls);
    }

    [Fact]
    public void Share_owner_ids_are_taken_from_the_directory()
    {
        Dictionary<string, string> env = new()
        {
            ["STAT_UID"] = "1000",
            ["STAT_GID"] = "1000",
            ["GROUP_NAME"] = "smurf123lol",
            ["GROUP_EXISTS"] = "1",
            ["SHARE_DIR"] = "$HOME/share",
        };

        GuardResult result = Run(
            """
            mkdir -p "$SHARE_DIR"
            parse_share "$SHARE_DIR"
            echo "rc=$?"
            """,
            env);

        Assert.Contains(
            "uid=1000 gid=1000 group=smurf123lol create=0",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_share_group_is_marked_for_creation()
    {
        // После переустановки группы с прежним GID нет: `useradd --gid 1000` упал бы
        // «group '1000' does not exist», поэтому группу надо создать самим.
        Dictionary<string, string> env = new()
        {
            ["STAT_UID"] = "1000",
            ["STAT_GID"] = "1000",
            ["GROUP_EXISTS"] = "0",
            ["SHARE_DIR"] = "$HOME/share",
        };

        GuardResult result = Run(
            """
            mkdir -p "$SHARE_DIR"
            parse_share "$SHARE_DIR"
            echo "rc=$?"
            """,
            env);

        Assert.Contains(
            "uid=1000 gid=1000 group= create=1",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Share_group_name_comes_from_gid_not_from_environment()
    {
        // Имя группы должно вычисляться по GID каталога. Если брать его из
        // окружения (или из имени пользователя), установщик добавит kouki
        // в группу, которой у шары нет, и «успех» окажется ложным.
        Dictionary<string, string> env = new()
        {
            ["STAT_UID"] = "1000",
            ["STAT_GID"] = "1000",
            ["GROUP_EXISTS"] = "0",
            ["GROUP_NAME"] = "some-other-group",
            ["SHARE_DIR"] = "$HOME/share",
        };

        GuardResult result = Run(
            """
            mkdir -p "$SHARE_DIR"
            parse_share "$SHARE_DIR"
            echo "rc=$?"
            """,
            env);

        Assert.Contains(
            "uid=1000 gid=1000 group= create=1",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Root_group_of_the_share_is_refused()
    {
        // Каталог на root-группе: добавлять kouki в root нельзя, шару надо
        // перевесить. Признак для установщика — create_group=2.
        Dictionary<string, string> env = new()
        {
            ["STAT_UID"] = "1000",
            ["STAT_GID"] = "0",
            ["GROUP_EXISTS"] = "1",
            ["GROUP_NAME"] = "root",
            ["SHARE_DIR"] = "$HOME/share",
        };

        GuardResult result = Run(
            """
            mkdir -p "$SHARE_DIR"
            parse_share "$SHARE_DIR"
            echo "rc=$?"
            """,
            env);

        Assert.Contains("uid=1000 gid=0 group= create=2", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("group=root", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Root_owned_share_keeps_its_group()
    {
        // Каталог root:smurf — uid 0, но группа шары обычная: SMB-пользователя
        // можно и нужно добавить в неё (иначе записи в 2775-каталог не будет).
        Dictionary<string, string> env = new()
        {
            ["STAT_UID"] = "0",
            ["STAT_GID"] = "1000",
            ["GROUP_EXISTS"] = "1",
            ["GROUP_NAME"] = "smurf123lol",
            ["SHARE_DIR"] = "$HOME/share",
        };

        GuardResult result = Run(
            """
            mkdir -p "$SHARE_DIR"
            parse_share "$SHARE_DIR"
            echo "rc=$?"
            """,
            env);

        Assert.Contains("uid= gid=1000 group=smurf123lol create=0", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("parse-failed", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void System_group_of_the_share_is_refused()
    {
        // gid 27 — sudo: членство даёт лишние права, шару надо перевесить.
        Dictionary<string, string> env = new()
        {
            ["STAT_UID"] = "1000",
            ["STAT_GID"] = "27",
            ["GROUP_EXISTS"] = "1",
            ["GROUP_NAME"] = "sudo",
            ["SHARE_DIR"] = "$HOME/share",
        };

        GuardResult result = Run(
            """
            mkdir -p "$SHARE_DIR"
            parse_share "$SHARE_DIR"
            echo "rc=$?"
            """,
            env);

        Assert.Contains("uid=1000 gid=27 group=sudo create=3", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_share_directory_falls_back_to_a_plain_user()
    {
        GuardResult result = Run(
            """
            parse_share /nonexistent-kouki-share
            echo "rc=$?"
            """,
            new Dictionary<string, string>());

        Assert.Contains("uid= gid= group= create=0", result.Stdout, StringComparison.Ordinal);
    }

    private sealed record GuardResult(int FunctionRc, string Stdout, string Stderr, string Calls);

    private static GuardResult Run(string body, Dictionary<string, string> env)
    {
        string? bash = FindBash();
        if (bash is null)
        {
            // Молча «пропустить» — значит оставить ветви установщика без проверки
            // и не заметить этого. Пусть падает громко: в CI bash есть всегда,
            // на Windows нужен Git Bash.
            throw new InvalidOperationException(
                "bash не найден: поведенческие тесты установщика не выполнить " +
                "(Windows — поставьте Git Bash, Linux — apt install bash)");
        }

        string dir = Directory.CreateTempSubdirectory("kouki-guards-").FullName;
        try
        {
            string calls = Path.Combine(dir, "calls.txt").Replace('\\', '/');
            File.WriteAllText(calls, string.Empty);

            string guards = TestPaths.Path("deploy", "lib", "install-guards.sh").Replace('\\', '/');
            string bodyPosix = body.Replace("\r\n", "\n").Trim();

            string script = $$"""
                set -u
                CALLS="{{calls}}"
                systemctl() {
                    echo "systemctl $*" >> "$CALLS"
                    case "$1" in
                        is-enabled) [ "${ENABLED:-0}" = "1" ] && return 0 || return 1 ;;
                        disable)    return "${DISABLE_RC:-0}" ;;
                        enable)     return "${ENABLE_RC:-0}" ;;
                        is-active)  [ "${ACTIVE:-1}" = "1" ] && return 0 || return 1 ;;
                        start)      return "${START_RC:-0}" ;;
                    esac
                    return 0
                }
                stat() {
                    case "$2" in
                        %u) echo "${STAT_UID:-1000}" ;;
                        %g) echo "${STAT_GID:-1000}" ;;
                        *)  echo "" ;;
                    esac
                }
                getent() {
                    # Группа существует независимо от того, знает ли её вызывающий:
                    # иначе тест не отличит «нашли по GID» от «взяли из имени».
                    if [ "$1" = "group" ]; then
                        [ "${GROUP_EXISTS:-1}" = "1" ] || return 2
                        echo "${GROUP_NAME:-smurf123lol}:x:${STAT_GID:-1000}:"
                        return 0
                    fi
                    return 2
                }
                command() {
                    if [ "$1" = "-v" ] && { [ "$2" = "sqlite3" ] || [ "$2" = "rsync" ]; }; then
                        [ "${TOOLS_PRESENT:-0}" = "1" ] && return 0
                        return 1
                    fi
                    builtin command "$@"
                }
                . "{{guards}}"
                # Печатает разобранные поля так, как их читает установщик:
                # именно эта связка (guard -> read_share_ids) когда-то «съезжала»,
                # потому что `read` с IFS=: выбрасывает ведущие разделители.
                parse_share() {
                    if ! read_share_ids "$(resolve_share_ids "$1")"; then
                        echo "parse-failed"
                        return 1
                    fi
                    echo "uid=$SHARE_UID gid=$SHARE_GID group=$SHARE_GROUP create=$SHARE_CREATE_GROUP"
                }
                {{bodyPosix}}
                """;

            // Скрипт должен быть с LF: bash на строке с CR ругается
            // «syntax error near unexpected token `{<CR>'», а исходник теста
            // на Windows хранится с CRLF.
            script = script.Replace("\r\n", "\n");

            // Скрипт запускается из временного каталога; путь к guards — по частям,
            // чтобы работало и с пробелом в пути («Kouki Workspace»).
            string scriptPath = Path.Combine(dir, "run.sh");
            File.WriteAllText(scriptPath, script + "\n", new UTF8Encoding(false));

            var start = new ProcessStartInfo(bash, ["--noprofile", "--norc", scriptPath.Replace('\\', '/')])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.Environment["HOME"] = dir.Replace('\\', '/');
            start.Environment["TOOLS_PRESENT"] = "1";
            foreach ((string key, string value) in env)
            {
                start.Environment[key] = value;
            }

            using Process process = Process.Start(start)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
            }

            int rc = 0;
            foreach (string line in stdout.Split('\n'))
            {
                if (line.StartsWith("rc=", StringComparison.Ordinal) &&
                    int.TryParse(line[3..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    rc = parsed;
                }
            }

            return new GuardResult(
                rc,
                stdout.Replace("rc=" + rc.ToString(CultureInfo.InvariantCulture), string.Empty, StringComparison.Ordinal).Trim(),
                stderr,
                File.Exists(calls) ? File.ReadAllText(calls) : string.Empty);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Временный каталог остался — не повод валить тест.
            }
        }
    }

    /// <summary>
    /// Bash из PATH на Windows — это WSL: у него другой корень и он не видит
    /// пути Windows, поэтому «bash --version сработал» ещё не значит, что файл
    /// guards будет найден.
    /// </summary>
    private static bool IsWsl(string bash)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using Process probe = new()
            {
                StartInfo = new ProcessStartInfo(bash)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            probe.StartInfo.ArgumentList.Add("--version");
            probe.Start();
            string stdout = probe.StandardOutput.ReadToEnd();
            string stderr = probe.StandardError.ReadToEnd();
            bool exited = probe.WaitForExit(5_000);
            if (!exited || probe.ExitCode != 0)
            {
                // Заглушка WSL без настроенного дистрибутива просто падает:
                // такой bash для тестов непригоден.
                return true;
            }

            return stdout.Contains("microsoft", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return true;
        }
    }
    [Fact]
    public void Root_owned_share_with_root_group_needs_owner_fix()
    {
        // root:root под 2775 — писать не сможет никто: это «перевесьте шару».
        Dictionary<string, string> env = new()
        {
            ["STAT_UID"] = "0",
            ["STAT_GID"] = "0",
            ["GROUP_NAME"] = "root",
            ["SHARE_DIR"] = "$HOME/share",
        };

        GuardResult result = Run(
            """
            mkdir -p "$SHARE_DIR"
            parse_share "$SHARE_DIR"
            echo "rc=$?"
            """,
            env);

        Assert.Contains("uid= gid= group= create=2", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Share_ids_survive_the_installer_parsing()
    {
        // Регрессия: `IFS=: read` срезает ведущий разделитель, поэтому старый
        // формат «::0» читался как uid=0, а «:1000:smurf:0» — со сдвигом полей.
        // Старая строка должна отвергаться, новая — разбираться без сдвигов.
        GuardResult result = Run(
            """
            read_share_ids "::0" && echo "старый-формат-принят" || echo "старый-формат-отвергнут"
            read_share_ids "kouki-id:-:1000:smurf123lol:0" || exit 9
            echo "uid=$SHARE_UID gid=$SHARE_GID group=$SHARE_GROUP create=$SHARE_CREATE_GROUP"
            echo "rc=$?"
            """,
            new Dictionary<string, string>());

        Assert.Contains("старый-формат-отвергнут", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("uid= gid=1000 group=smurf123lol create=0", result.Stdout, StringComparison.Ordinal);
    }
    [Fact]
    public void Unknown_system_group_at_the_boundary_is_refused()
    {
        // SYS_GID_MAX по умолчанию 999 и сравнение включительное: gid 999 без
        // имени в /etc/group — тоже системная группа (на Ubuntu это journal).
        Dictionary<string, string> env = new()
        {
            ["STAT_UID"] = "1000",
            ["STAT_GID"] = "999",
            ["GROUP_EXISTS"] = "0",
            ["SHARE_DIR"] = "$HOME/share",
        };

        GuardResult result = Run(
            """
            mkdir -p "$SHARE_DIR"
            parse_share "$SHARE_DIR"
            echo "rc=$?"
            """,
            env);

        Assert.Contains("uid=1000 gid=999 group= create=3", result.Stdout, StringComparison.Ordinal);
    }
    [Fact]
    public void Empty_create_field_is_still_a_valid_line()
    {
        // Контракт: «-» — пустое значение для любого поля, включая create_group.
        // Раньше последнее `[ … ] && VAR=…` в функции возвращало 1, и валидная
        // строка с пустым create_group считалась ошибкой разбора.
        GuardResult result = Run(
            """
            if read_share_ids "kouki-id:-:1000:smurf123lol:-"; then
                echo "ok uid=$SHARE_UID gid=$SHARE_GID create=$SHARE_CREATE_GROUP"
            else
                echo "fail"
            fi
            echo "rc=$?"
            """,
            new Dictionary<string, string>());

        Assert.Contains("ok uid= gid=1000 create=", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("fail", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Broken_line_resets_the_previous_values()
    {
        // Установщик идёт под `set -u`: после ошибки разбора он не должен
        // увидеть значения от предыдущего вызова.
        GuardResult result = Run(
            """
            read_share_ids "kouki-id:-:1000:smurf123lol:0" || exit 9
            if read_share_ids "::0"; then echo "принят"; else echo "отвергнут"; fi
            echo "uid=[${SHARE_UID}] gid=[${SHARE_GID}] create=[${SHARE_CREATE_GROUP}]"
            echo "rc=$?"
            """,
            new Dictionary<string, string>());

        Assert.Contains("отвергнут", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("uid=[] gid=[] create=[]", result.Stdout, StringComparison.Ordinal);
    }
    [Fact]
    public void Create_share_user_passes_group_only_when_known()
    {
        // Пустой gid не должен превращаться в `useradd --gid ""`:
        // `group '' does not exist` роняло установку при несмонтированной шаре.
        Dictionary<string, string> env = new() { ["T_UID"] = "", ["T_GID"] = "" };

        GuardResult result = Run(
            """
            useradd() { echo "useradd $*" >> "$CALLS"; return 0; }
            create_share_user svc "${T_UID:-}" "${T_GID:-}"
            echo "rc=$?"
            """,
            env);

        Assert.Contains("useradd", result.Calls, StringComparison.Ordinal);
        Assert.DoesNotContain("--gid", result.Calls, StringComparison.Ordinal);
        Assert.DoesNotContain("--uid", result.Calls, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_share_user_takes_ids_when_known()
    {
        Dictionary<string, string> env = new() { ["T_UID"] = "1000", ["T_GID"] = "1000" };

        GuardResult result = Run(
            """
            useradd() { echo "useradd $*" >> "$CALLS"; return 0; }
            create_share_user svc "${T_UID:-}" "${T_GID:-}"
            echo "rc=$?"
            """,
            env);

        Assert.Contains("--uid 1000", result.Calls, StringComparison.Ordinal);
        Assert.Contains("--gid 1000", result.Calls, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_share_user_keeps_group_without_owner_id()
    {
        // Каталог root:smurf: uid передавать нельзя, группу — надо.
        Dictionary<string, string> env = new() { ["T_UID"] = "", ["T_GID"] = "1000" };

        GuardResult result = Run(
            """
            useradd() { echo "useradd $*" >> "$CALLS"; return 0; }
            create_share_user svc "${T_UID:-}" "${T_GID:-}"
            echo "rc=$?"
            """,
            env);

        Assert.Contains("--gid 1000", result.Calls, StringComparison.Ordinal);
        Assert.DoesNotContain("--uid", result.Calls, StringComparison.Ordinal);
    }
    private static string? FindBash()
    {
        foreach (string candidate in new[]
                 {
                     // Git Bash первым: bash из PATH на Windows — заглушка WSL,
                     // которая не видит пути Windows.
                     @"C:\Program Files\Git\bin\bash.exe",
                     @"C:\Program Files\Git\usr\bin\bash.exe",
                     "bash",
                 })
        {
            try
            {
                var probe = new ProcessStartInfo(candidate, ["-c", "exit 0"])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using Process? process = Process.Start(probe);
                if (process is null)
                {
                    continue;
                }

                if (!process.WaitForExit(10_000))
                {
                    process.Kill(entireProcessTree: true);
                    continue;
                }

                if (process.ExitCode == 0)
                {
                    // bash из PATH на Windows — это WSL: он видит другой корень
                    // файловой системы, и путь к guards в нём просто не существует.
                    if (IsWsl(candidate))
                    {
                        continue;
                    }

                    return candidate;
                }
            }
            catch (Exception)
            {
                // Нет такого bash — пробуем следующий вариант.
            }
        }

        return null;
    }
}
