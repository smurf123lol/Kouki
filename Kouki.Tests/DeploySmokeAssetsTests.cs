namespace Kouki.Tests;

/// <summary>
/// Контракт смоука и сторожа (T85, T53). Shell и systemd тестами не покрыть,
/// но обязательные свойства текстов — можно: именно они ломаются при правках.
/// </summary>
public sealed class DeploySmokeAssetsTests
{
    private static string LocateDeployFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "deploy", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Не нашёл deploy/{name} выше {AppContext.BaseDirectory}");
    }

    private static string DeployFile(string name) => File.ReadAllText(LocateDeployFile(name));

    [Fact]
    public void Smoke_script_is_defensive_and_reads_the_token_from_env_file()
    {
        var script = DeployFile("smoke.sh");

        Assert.Contains("set -euo pipefail", script, StringComparison.Ordinal);
        Assert.Contains("Kouki__Token=", script, StringComparison.Ordinal);
        Assert.DoesNotContain("echo \"$TOKEN\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Scripts_do_not_put_secrets_into_the_command_line()
    {
        var smoke = DeployFile("smoke.sh");
        var monitor = DeployFile("kouki-monitor.sh");

        // Токен уходит в файл конфига 600 и передаётся через curl -K: иначе он
        // виден любому локальному пользователю в ps и /proc/<pid>/cmdline.
        Assert.Contains("-K \"$AUTH_CONFIG\"", smoke, StringComparison.Ordinal);
        Assert.Contains("-K \"$conf\"", monitor, StringComparison.Ordinal);
        Assert.Contains("chmod 600", smoke, StringComparison.Ordinal);
        Assert.Contains("chmod 600", monitor, StringComparison.Ordinal);

        Assert.DoesNotContain("-H \"Authorization: Bearer", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("-H \"Authorization: Bearer", monitor, StringComparison.Ordinal);
        // Токен бота не должен собираться в URL прямо в аргументах curl.
        Assert.DoesNotContain("curl -s -X POST \"https://api.telegram.org", monitor, StringComparison.Ordinal);
    }

    [Fact]
    public void Smoke_script_checks_the_things_that_broke_after_the_move()
    {
        var script = DeployFile("smoke.sh");

        Assert.Contains("/api/health", script, StringComparison.Ordinal);
        Assert.Contains("/api/classifiers", script, StringComparison.Ordinal);
        Assert.Contains("/api/speech", script, StringComparison.Ordinal);
        Assert.Contains("/api/reminders/due", script, StringComparison.Ordinal);
        Assert.Contains("systemctl is-active", script, StringComparison.Ordinal);
        Assert.Contains("kouki-backup.timer", script, StringComparison.Ordinal);
        Assert.Contains("/srv/files", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Smoke_is_read_only_by_default_and_writes_only_with_the_flag()
    {
        var script = DeployFile("smoke.sh");

        Assert.Contains("WITH_WRITES=0", script, StringComparison.Ordinal);
        Assert.Contains("--with-writes", script, StringComparison.Ordinal);

        // Проверки с записью в базу стоят внутри ветки --with-writes.
        var writes = script.IndexOf("if [ \"$WITH_WRITES\" -eq 1 ]", StringComparison.Ordinal);
        var intake = script.IndexOf("\"$BASE_URL/api/intake\"", StringComparison.Ordinal);
        Assert.True(writes > 0 && intake > writes, "POST /api/intake должен быть под --with-writes");
    }

    [Fact]
    public void Smoke_gives_the_answerer_and_speech_their_own_timeouts()
    {
        var script = DeployFile("smoke.sh");

        Assert.Contains("INTAKE_TIMEOUT=", script, StringComparison.Ordinal);
        Assert.Contains("SPEECH_TIMEOUT=", script, StringComparison.Ordinal);
        Assert.Contains("\"$INTAKE_TIMEOUT\"", script, StringComparison.Ordinal);
        Assert.Contains("\"$SPEECH_TIMEOUT\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Smoke_reports_failures_with_nonzero_exit()
    {
        var script = DeployFile("smoke.sh");

        Assert.Contains("exit 1", script, StringComparison.Ordinal);
        Assert.Contains("FAILURES", script, StringComparison.Ordinal);
        Assert.Contains("--json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Smoke_script_has_help_and_unknown_argument_handling()
    {
        var script = DeployFile("smoke.sh");

        Assert.Contains("usage()", script, StringComparison.Ordinal);
        Assert.Contains("--help", script, StringComparison.Ordinal);
        Assert.Contains("Неизвестный аргумент", script, StringComparison.Ordinal);
        Assert.Contains("exit 2", script, StringComparison.Ordinal);
        // Аргумент без значения — понятная ошибка, а не «unbound variable».
        Assert.Contains("need_value", script, StringComparison.Ordinal);
        Assert.Contains("требует значения", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitor_script_keeps_state_and_does_not_alert_on_every_check()
    {
        var script = DeployFile("kouki-monitor.sh");

        Assert.Contains("set -euo pipefail", script, StringComparison.Ordinal);
        Assert.Contains("STATE_FILE", script, StringComparison.Ordinal);
        Assert.Contains("REMIND_AFTER", script, StringComparison.Ordinal);
        Assert.Contains("notify", script, StringComparison.Ordinal);
        Assert.Contains("SuccessExitStatus", DeployFile("kouki-monitor.service"), StringComparison.Ordinal);
    }

    [Fact]
    public void Monitor_treats_a_missing_api_token_as_a_problem()
    {
        var script = DeployFile("kouki-monitor.sh");

        Assert.Contains("нет токена API", script, StringComparison.Ordinal);
        Assert.Contains("/api/classifiers", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitor_writes_the_alert_stamp_only_after_delivery()
    {
        var script = DeployFile("kouki-monitor.sh");

        // notify возвращает статус, а отметка времени пишется внутри проверки.
        Assert.Contains("if notify ", script, StringComparison.Ordinal);
        Assert.Contains("STAMP_FILE", script, StringComparison.Ordinal);
        Assert.Contains("return 1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitor_does_not_print_ok_every_five_minutes()
    {
        var script = DeployFile("kouki-monitor.sh");

        Assert.Contains("if [ \"$previous\" != \"ok\" ]", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitor_does_not_echo_secrets_to_the_journal()
    {
        var script = DeployFile("kouki-monitor.sh");

        Assert.DoesNotContain("log \"$KOUKI_ALERT_TOKEN\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("log \"$TOKEN\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitor_timer_runs_every_five_minutes()
    {
        var timer = DeployFile("kouki-monitor.timer");

        Assert.Contains("OnUnitActiveSec=5min", timer, StringComparison.Ordinal);
        Assert.Contains("Unit=kouki-monitor.service", timer, StringComparison.Ordinal);
        Assert.Contains("WantedBy=timers.target", timer, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_installs_the_monitor_and_its_unit_points_to_the_installed_path()
    {
        var installer = DeployFile("install.sh");

        Assert.Contains("kouki-monitor.sh", installer, StringComparison.Ordinal);
        Assert.Contains("kouki-monitor.timer", installer, StringComparison.Ordinal);
        Assert.Contains("/usr/local/bin/kouki-monitor.sh", installer, StringComparison.Ordinal);
        Assert.Contains("--no-monitor", installer, StringComparison.Ordinal);
        Assert.Contains("/usr/local/bin/kouki-monitor.sh",
            DeployFile("kouki-monitor.service"), StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_judges_the_monitor_probe_by_state_not_by_exit_code()
    {
        var installer = DeployFile("install.sh");

        Assert.Contains("/var/lib/kouki/monitor/state", installer, StringComparison.Ordinal);
        Assert.Contains("case \"$MONITOR_STATE\"", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_scripts_start_with_a_shebang_and_no_byte_order_mark()
    {
        foreach (var name in new[] { "smoke.sh", "kouki-monitor.sh" })
        {
            var head = File.ReadAllBytes(LocateDeployFile(name)).Take(2).ToArray();
            Assert.Equal((byte)'#', head[0]);
            Assert.Equal((byte)'!', head[1]);
        }
    }
}
