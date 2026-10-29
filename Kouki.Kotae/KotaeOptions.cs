namespace Kouki.Kotae;

/// <summary>Кто отвечает на вопросы.</summary>
public enum KotaeProvider
{
    /// <summary>Никто: команда <c>ask</c> остаётся заглушкой.</summary>
    None = 0,

    /// <summary>Google AI Studio / Gemini API (ключ из <c>aistudio.google.com</c>).</summary>
    Gemini = 1,
}

/// <summary>
/// Настройки ответчика. Читаются из секции <c>Kouki:Answerer</c>
/// (в окружении — <c>Kouki__Answerer__ApiKey</c> и т.п.).
/// </summary>
/// <remarks>
/// Ключ здесь, а не в репозитории: в git он не попадает, на сервере живёт
/// в <c>/etc/kouki/kouki.env</c>. Провайдер по умолчанию — <see cref="KotaeProvider.None"/>,
/// чтобы забытый ключ не отправлял приватный текст наружу.
/// </remarks>
public sealed class KotaeOptions
{
    /// <summary>Провайдер. По умолчанию выключен.</summary>
    public KotaeProvider Provider { get; set; } = KotaeProvider.None;

    /// <summary>Ключ API провайдера.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Модель. Для Gemini — <c>gemini-2.5-flash</c> (быстрая и дешёвая).</summary>
    public string Model { get; set; } = "gemini-2.5-flash";

    /// <summary>Базовый адрес API (без последней части пути).</summary>
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta";

    /// <summary>Сколько ждать ответа.</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Предел длины ответа в токенах: голосовому ответу больше не нужно.</summary>
    public int MaxOutputTokens { get; set; } = 512;

    /// <summary>Системная подсказка: русский язык и короткие ответы.</summary>
    public string SystemPrompt { get; set; } =
        "Ты — Kouki, голосовой ассистент. Отвечай по-русски, коротко и по делу: " +
        "две-три фразы, без списков и без markdown. Если не знаешь — скажи прямо.";

    /// <summary>
    /// Проверяет настройки и объясняет, что не так. <c>null</c> — всё готово.
    /// </summary>
    public string? Validate()
    {
        if (Provider == KotaeProvider.None)
        {
            return "ответчик выключен: Kouki:Answerer:Provider = None";
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            return "не задан ключ: Kouki:Answerer:ApiKey";
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            return "не задана модель: Kouki:Answerer:Model";
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return "Endpoint должен быть абсолютным https-адресом";
        }

        if (TimeoutSeconds is < 1 or > 120)
        {
            return "TimeoutSeconds должен быть от 1 до 120";
        }

        if (MaxOutputTokens is < 16 or > 8192)
        {
            return "MaxOutputTokens должен быть от 16 до 8192";
        }

        return null;
    }
}
