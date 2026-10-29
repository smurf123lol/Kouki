namespace Kouki.Yasabi;

/// <summary>
/// Настройки классификатора на языковой модели: локальная модель (llama.cpp, Ollama)
/// или любой OpenAI-совместимый сервис.
/// </summary>
/// <remarks>
/// Модель понимает больше формулировок, чем правила, но отвечает медленнее:
/// локальная 4B — единицы секунд. Поэтому по умолчанию она только сравнивается
/// с правилами в дашборде, а приём идёт через правила. Переключение — <see cref="UseForIntake"/>.
/// </remarks>
public sealed class LlmIntentOptions
{
    /// <summary>Подключать классификатор: без этого он не попадает даже в сравнение.</summary>
    public bool Enabled { get; set; }

    /// <summary>Использовать модель и в приёме, а не только в сравнении.</summary>
    public bool UseForIntake { get; set; }

    /// <summary>Адрес OpenAI-совместимого API: llama-server отдаёт <c>http://127.0.0.1:8080/v1</c>.</summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:8080/v1";

    /// <summary>Имя модели в API (у llama-server — <c>--alias</c>).</summary>
    public string Model { get; set; } = "qwen3";

    /// <summary>Ключ; локальному серверу он не нужен, но поле должно быть непустым для Bearer.</summary>
    public string ApiKey { get; set; } = "local";

    /// <summary>Сколько ждать ответа модели.</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Предел ответа: нам нужен короткий JSON, а не рассуждение.</summary>
    public int MaxOutputTokens { get; set; } = 32;

    /// <summary>Интенты, которые модель имеет право вернуть.</summary>
    public string[] Intents { get; set; } = LlmIntentClassifier.DefaultIntents;
}
