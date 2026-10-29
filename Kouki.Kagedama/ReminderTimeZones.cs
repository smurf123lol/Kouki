namespace Kouki.Kagedama;

/// <summary>
/// Часовой пояс напоминаний: один на процесс, задаётся настройкой хоста.
/// </summary>
public static class ReminderTimeZones
{
    /// <summary>Переменная окружения, если настройка хоста не задана.</summary>
    public const string EnvironmentVariable = "KOUKI_TIMEZONE";

    /// <summary>
    /// Разрешает пояс: настройка → переменная окружения → пояс сервера.
    /// </summary>
    /// <param name="configured">Пояс из настроек или <c>null</c>.</param>
    public static TimeZoneInfo Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return FromName(configured);
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? TimeZoneInfo.Local : FromName(fromEnvironment);
    }

    /// <summary>
    /// Разбирает имя пояса; неизвестное имя не роняет сервер, а даёт пояс машины.
    /// </summary>
    /// <param name="name">IANA или имя Windows.</param>
    public static TimeZoneInfo FromName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string value = name.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(value);
        }
        catch (TimeZoneNotFoundException)
        {
            // На Linux нет имён Windows: пробуем перевести и не падаем.
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(value, out string? windows) && windows is not null)
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windows);
            }
        }
        catch (InvalidTimeZoneException)
        {
            // Битое описание пояса в системе — работаем по поясу машины.
        }

        return TimeZoneInfo.Local;
    }
}
