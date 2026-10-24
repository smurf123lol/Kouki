using Microsoft.Extensions.Logging;

namespace Kouki.Kagedama.Hosting;

/// <summary>
/// Приводит пути из конфигурации к абсолютным и делает это одинаково для всех хостов.
/// </summary>
/// <remarks>
/// Зачем: на сервере «рядом с dll» и «в рабочем каталоге» — разные места. Относительный путь
/// раньше разрешался от текущего каталога процесса, поэтому при запуске из другого каталога
/// (systemd, ярлык, отладчик) база, аудио и модель оказывались в неожиданных местах. Здесь
/// относительный путь разрешается от каталога приложения, а на сервере его можно запретить
/// настройкой <c>Kouki:RequireAbsolutePaths=true</c> — тогда старт падает с внятным текстом,
/// вместо того чтобы молча создать базу не там.
/// </remarks>
public static class KoukiPaths
{
    /// <summary>
    /// Возвращает абсолютный путь: пустое значение — значение по умолчанию рядом с приложением,
    /// абсолютный путь — как есть, относительный — от каталога приложения.
    /// </summary>
    /// <param name="value">Значение из конфигурации (может быть пустым).</param>
    /// <param name="fallbackRelativeToBaseDirectory">Имя по умолчанию относительно каталога приложения.</param>
    /// <param name="settingName">Имя настройки для сообщений об ошибке.</param>
    /// <param name="requireAbsolute">Запретить относительные пути.</param>
    /// <param name="logger">Куда писать предупреждение о разрешённом относительном пути.</param>
    /// <exception cref="InvalidOperationException">Относительный путь при <paramref name="requireAbsolute"/>.</exception>
    public static string Resolve(
        string? value,
        string fallbackRelativeToBaseDirectory,
        string settingName,
        bool requireAbsolute = false,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackRelativeToBaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingName);

        string? configured = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        if (configured is not null && Path.IsPathRooted(configured))
        {
            return Path.GetFullPath(configured);
        }

        if (requireAbsolute)
        {
            throw new InvalidOperationException(
                $"Путь '{settingName}' должен быть абсолютным, а получено '{configured ?? "(не задан)"}'. " +
                $"Задайте его в конфигурации (Kouki:{settingName}) или переменной окружения " +
                $"Kouki__{settingName}, например /var/lib/kouki/{fallbackRelativeToBaseDirectory}.");
        }

        string resolved = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured ?? fallbackRelativeToBaseDirectory));

        if (configured is not null)
        {
            logger?.LogWarning(
                "Путь {Setting} = '{Value}' относительный: разрешён от каталога приложения как {Resolved}. " +
                "На сервере задавайте абсолютный путь.",
                settingName,
                configured,
                resolved);
        }

        return resolved;
    }
}
