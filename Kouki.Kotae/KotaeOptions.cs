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

    /// <summary>
    /// Просит модель не рассуждать вслух (<c>chat_template_kwargs.enable_thinking = false</c>).
    /// </summary>
    /// <remarks>
    /// Нужно локальным «думающим» моделям вроде Nemotron: с включённым размышлением они
    /// кладут в <c>content</c> английский план решения, а на голосовой станции это
    /// читается как абзац бессвязной речи. Флаг понимает llama.cpp (шаблон чата модели),
    /// остальные провайдеры молча игнорируют лишний ключ.
    /// </remarks>
    public bool DisableThinking { get; set; }

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

        if (!Uri.TryCreate(ResolveEndpoint(), UriKind.Absolute, out var uri))
        {
            return "Endpoint должен быть абсолютным адресом";
        }

        // Наружу — только https: приватный текст нельзя гонять открытым текстом.
        // Локальная модель (llama-server, ollama) живёт на loopback и там http нормален.
        if (uri.Scheme != Uri.UriSchemeHttps && !IsLocal(uri))
        {
            return "Endpoint должен быть https-адресом (http разрешён только для 127.0.0.1/localhost)";
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

    /// <summary>Локальный адрес: loopback или частная сеть — там http допустим.</summary>
    private static bool IsLocal(Uri uri)
        => uri.IsLoopback
           || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || (uri.HostNameType == UriHostNameType.IPv4 && IsPrivateIpv4(uri.Host));

    private static bool IsPrivateIpv4(string host)
    {
        var parts = host.Split('.');
        if (parts.Length != 4 || !byte.TryParse(parts[0], out var a) || !byte.TryParse(parts[1], out var b))
        {
            return false;
        }

        return a == 10
               || (a == 192 && b == 168)
               || (a == 172 && b is >= 16 and <= 31);
    }
}
