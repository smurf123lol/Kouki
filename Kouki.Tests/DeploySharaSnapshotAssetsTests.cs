using System.Text.RegularExpressions;

namespace Kouki.Tests;

/// <summary>
/// Проверки артефактов резервных снимков сетевой шары (T100).
/// Проверяются скрипты, юниты systemd, конфиги и документация на соответствие спецификации.
/// </summary>
public sealed class DeploySharaSnapshotAssetsTests
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
    public void Shara_snapshot_script_exists_and_is_defensive()
    {
        var script = DeployFile("shara-snapshot.sh");

        Assert.Contains("set -euo pipefail", script);
        Assert.Contains("flock", script, StringComparison.Ordinal);
        Assert.Contains("mountpoint", script, StringComparison.Ordinal);
        Assert.Contains("--link-dest", script, StringComparison.Ordinal);
        Assert.Contains("cp -al", script, StringComparison.Ordinal);
        Assert.Contains("usage()", script, StringComparison.Ordinal);
        Assert.Contains("--self-test", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Shara_snapshot_script_contains_all_spec_commands()
    {
        var script = DeployFile("shara-snapshot.sh");

        Assert.Contains("cmd_snapshot()", script, StringComparison.Ordinal);
        Assert.Contains("cmd_status()", script, StringComparison.Ordinal);
        Assert.Contains("cmd_restore()", script, StringComparison.Ordinal);
        Assert.Contains("cmd_verify()", script, StringComparison.Ordinal);
        Assert.Contains("cmd_prune()", script, StringComparison.Ordinal);
        Assert.Contains("cmd_self_test()", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Shara_snapshot_script_contains_exact_error_messages_from_spec()
    {
        var script = DeployFile("shara-snapshot.sh");

        Assert.Contains("не смонтирован: запустите systemctl start srv-files.mount", script);
        Assert.Contains("нет предыдущих версий: первый снимок будет полным", script);
        Assert.Contains("не найдена. Доступны:", script);
        Assert.Contains("не найден в версии", script);
        Assert.Contains("вне шары: восстановление только из /srv/files", script);
        Assert.Contains("снимки больше лимита, нужен prune или чистка", script);
        Assert.Contains("не найден ни один файл для проверки", script);
    }

    [Fact]
    public void Shara_snapshot_service_has_idle_io_and_timeout()
    {
        var unit = DeployFile("shara-snapshot.service");

        Assert.Contains("Type=oneshot", unit);
        Assert.Contains("Nice=10", unit);
        Assert.Contains("IOSchedulingClass=idle", unit);
        Assert.Contains("TimeoutStartSec=6h", unit);
        Assert.Contains("ExecStart=/usr/bin/bash /opt/kouki/app/deploy/shara-snapshot.sh snapshot", unit);
        Assert.Contains("EnvironmentFile=-/etc/kouki/shara-snapshot.env", unit);
        Assert.Contains("ProtectSystem=strict", unit);
    }

    [Fact]
    public void Shara_snapshot_timer_is_daily_with_delay_and_persistent()
    {
        var timer = DeployFile("shara-snapshot.timer");

        Assert.Contains("OnCalendar=*-*-* 03:30:00", timer);
        Assert.Contains("Persistent=true", timer);
        Assert.Contains("RandomizedDelaySec=10m", timer);
        Assert.Contains("Unit=shara-snapshot.service", timer);
    }

    [Fact]
    public void Shara_snapshot_env_example_matches_spec_defaults()
    {
        var env = DeployFile("shara-snapshot.env.example");

        Assert.Contains("SRC=/srv/files", env);
        Assert.Contains("DEST=/srv/files/_backup/snapshots", env);
        Assert.Contains("KEEP_DAILY=7", env);
        Assert.Contains("KEEP_WEEKLY=4", env);
        Assert.Contains("MAX_AGE_HOURS=36", env);
        Assert.Contains("MAX_USAGE_PERCENT=10", env);
        Assert.Contains("EXCLUDE_FILE=/etc/kouki/shara-exclude.txt", env);
        Assert.Contains("VERIFY_SAMPLES=20", env);
    }

    [Fact]
    public void Shara_exclude_file_excludes_backups_and_temp()
    {
        var exclude = DeployFile("shara-exclude.txt");

        Assert.Contains("_backup/", exclude);
        Assert.Contains("lost+found/", exclude);
        Assert.Contains("_kouki/tmp/", exclude);
    }

    [Fact]
    public void Installer_supports_shara_snapshots_and_dry_run()
    {
        var installer = DeployFile("install.sh");

        Assert.Contains("--with-shara-snapshots", installer);
        Assert.Contains("--no-shara-snapshots", installer);
        Assert.Contains("--dry-run", installer);
        Assert.Contains("shara-snapshot.timer", installer);
        Assert.Contains("shara-snapshot.service", installer);
        Assert.Contains("shara-snapshot.env", installer);
        Assert.Contains("shara-exclude.txt", installer);
    }

    [Fact]
    public void Deploy_readme_documents_shara_snapshots()
    {
        var readme = DeployFile("README.md");

        Assert.Contains("Снимки сетевой шары /srv/files (T100)", readme);
        Assert.Contains("shara-snapshot.sh", readme);
        Assert.Contains("shara-snapshot.timer", readme);
        Assert.Contains("restore", readme);
        Assert.Contains("status", readme);
        Assert.Contains("verify", readme);
        Assert.Contains("prune", readme);
        Assert.Contains("--self-test", readme);
    }
}
