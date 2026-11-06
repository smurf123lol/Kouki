using Kouki.Shimi.ServiceAbstractions;

namespace Kouki.Un;

/// <summary>
/// Заглушка синтеза речи: сборка без <c>-p:KoukiEnableSherpa=true</c> либо
/// сборка с sherpa, но без каталога голоса.
/// </summary>
/// <remarks>
/// Хост спрашивает <see cref="IsConfigured"/> до того, как звать синтез:
/// поэтому заглушка не бросает в конструкторе, а объясняет причину.
/// </remarks>
public sealed class UnavailableSpeechSynthesizer : ISpeechSynthesizer
{
    /// <summary>Причина, по которой синтез недоступен.</summary>
    public const string Reason =
        "Озвучивание ответов ещё не подключено: сборка без пакета sherpa-onnx " +
        "(dotnet build -p:KoukiEnableSherpa=true).";

    private readonly string _reason;

    /// <summary>Создаёт заглушку с причиной по умолчанию либо с конкретной.</summary>
    /// <param name="reason">
    /// Человеческое объяснение, почему синтеза нет (каталог голоса не найден и т.п.).
    /// <c>null</c> — причина по умолчанию (<see cref="Reason"/>).
    /// </param>
    public UnavailableSpeechSynthesizer(string? reason = null)
    {
        _reason = string.IsNullOrWhiteSpace(reason) ? Reason : reason;
    }

    /// <inheritdoc />
    public bool IsConfigured => false;

    /// <inheritdoc />
    public string? UnavailableReason => _reason;

    /// <inheritdoc />
    public Task<SpeechSynthesisResult> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(_reason);

    /// <inheritdoc />
    public Task<SpeechSynthesisResult> SynthesizeAsync(string text, string? voice, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(_reason);
}
