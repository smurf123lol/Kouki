namespace Kouki.Kotae;

/// <summary>Кто отвечает на вопросы.</summary>
public enum KotaeProvider
{
    /// <summary>Никто: команда <c>ask</c> остаётся заглушкой.</summary>
    None = 0,

    /// <summary>Google AI Studio / Gemini API (ключ из <c>aistudio.google.com</c>).</summary>
    Gemini = 1,

    /// <summary>
    /// Любой сервис с OpenAI-совместимым <c>/chat/completions</c>: DeepSeek, Mistral,
    /// OpenRouter, свой шлюз. Практичнее Gemini: тот отказывает по региону
    /// («User location is not supported for the API use»).
    /// </summary>
    OpenAi = 2,
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
    /// <summary>Адрес Gemini API по умолчанию.</summary>
    public const string GeminiEndpoint = "https://generativelanguage.googleapis.com/v1beta";

    /// <summary>Адрес OpenAI-совместимого API по умолчанию (DeepSeek).</summary>
    public const string OpenAiEndpoint = "https://api.deepseek.com/v1";

    /// <summary>Провайдер. По умолчанию выключен.</summary>
    public KotaeProvider Provider { get; set; } = KotaeProvider.None;

    /// <summary>Ключ API провайдера.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Модель; <c>null</c> — взять умолчание провайдера.</summary>
    public string? Model { get; set; }

    /// <summary>Базовый адрес API; <c>null</c> — взять умолчание провайдера.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Сколько ждать ответа.</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Предел длины ответа в токенах: голосовому ответу больше не нужно.</summary>
    public int MaxOutputTokens { get; set; } = 512;

    /// <summary>Системная подсказка: русский язык и короткие ответы.</summary>
    public string SystemPrompt { get; set; } =
        "Ты — Kouki, голосовой ассистент. Отвечай по-русски, коротко и по делу: " +
        "две-три фразы, без списков и без markdown. Если не знаешь — скажи прямо.";

    /// <summary>Модель с подставленным умолчанием провайдера.</summary>
    public string ResolveModel()
        => string.IsNullOrWhiteSpace(Model)
            ? Provider switch
            {
                KotaeProvider.Gemini => "gemini-3.8-flash",
                KotaeProvider.OpenAi => "deepseek-chat",
                _ => string.Empty,
            }
            : Model.Trim();

    /// <summary>Адрес API с подставленным умолчанием провайдера.</summary>
    public string ResolveEndpoint()
        => string.IsNullOrWhiteSpace(Endpoint)
            ? Provider switch
            {
                KotaeProvider.Gemini => GeminiEndpoint,
                KotaeProvider.OpenAi => OpenAiEndpoint,
                _ => string.Empty,
            }
            : Endpoint.Trim().TrimEnd('/');

    /// <summary>
    /// Проверяет настройки и объясняет, что не так. <c>null</c> — всё готово.
    /// </summary>
    public string? Validate()
    {
        if (Provider == KotaeProvider.None)
        {
            return "ответчик выключен: Kouki:Answerer:Provider = None";
        }

        if (!Enum.IsDefined(Provider))
        {
            return $"неизвестный провайдер: {(int)Provider}";
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            return "не задан ключ: Kouki:Answerer:ApiKey (на сервере — /etc/kouki/kouki.env)";
        }

        if (string.IsNullOrWhiteSpace(ResolveModel()))
        {
            return "не задана модель: Kouki:Answerer:Model";
        }

        if (!Uri.TryCreate(ResolveEndpoint(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
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
