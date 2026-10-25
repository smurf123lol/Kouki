using System.Text.RegularExpressions;

namespace Kouki.Tests;

/// <summary>
/// Контракт резервного копирования (T50). Проверяется текст файлов деплоя: shell и
/// systemd тестами не покрыть, но их обязательные свойства — можно.
/// </summary>
public sealed class DeployBackupAssetsTests
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
    public void Backup_script_exists_and_is_defensive()
    {
        var script = DeployFile("kouki-backup.sh");

        Assert.Contains("set -euo pipefail", script);
        Assert.Contains("flock", script, StringComparison.Ordinal);
        Assert.Contains("--retention", script, StringComparison.Ordinal);
        Assert.Contains("usage()", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Backup_script_uses_sqlite_backup_not_file_copy()
    {
        var script = DeployFile("kouki-backup.sh");

        Assert.Contains(".backup", script, StringComparison.Ordinal);
        Assert.Contains("integrity_check", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\bcp\s+[^\n]*\.sqlite"), script);
    }

    [Fact]
    public void Backup_script_copies_audio_and_prunes_by_retention()
    {
        var script = DeployFile("kouki-backup.sh");

        Assert.Contains("rsync", script, StringComparison.Ordinal);
        Assert.Contains("find \"$BACKUP_DIR/db\"", script, StringComparison.Ordinal);
        Assert.Contains("-mtime", script, StringComparison.Ordinal);
        Assert.Contains("-delete", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Backup_service_runs_script_as_kouki_user()
    {
        var unit = DeployFile("kouki-backup.service");

        Assert.Contains("User=kouki", unit);
        Assert.Contains("ExecStart=/usr/bin/bash /opt/kouki/app/deploy/kouki-backup.sh", unit);
        Assert.Contains("EnvironmentFile=/etc/kouki/backup.env", unit);
        Assert.Contains("NoNewPrivileges=true", unit);
    }

    [Fact]
    public void Backup_timer_is_daily_and_persistent()
    {
        var timer = DeployFile("kouki-backup.timer");

        Assert.Contains("OnCalendar=", timer, StringComparison.Ordinal);
        Assert.Contains("Persistent=true", timer);
        Assert.Contains("Unit=kouki-backup.service", timer);
    }

    [Fact]
    public void Backup_env_example_has_destination_and_retention()
    {
        var env = DeployFile("backup.env.example");

        Assert.Contains("KOUKI_BACKUP_DIR=", env, StringComparison.Ordinal);
        Assert.Contains("KOUKI_BACKUP_RETENTION_DAYS=", env, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_readme_explains_backup_and_restore()
    {
        var readme = DeployFile("README.md");

        Assert.Contains("kouki-backup.timer", readme, StringComparison.Ordinal);
        Assert.Contains("systemctl enable --now", readme, StringComparison.Ordinal);
        Assert.Contains("Восстановление", readme, StringComparison.Ordinal);
    }
}
