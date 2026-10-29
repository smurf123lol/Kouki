using Kouki.Shimi.ServiceAbstractions;

namespace Kouki.Un;

/// <summary>
/// Заглушка синтеза речи: сборка без <c>-p:KoukiEnableSherpa=true</c>.
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

    /// <inheritdoc />
    public bool IsConfigured => false;

    /// <inheritdoc />
    public string? UnavailableReason => Reason;

    /// <inheritdoc />
    public Task<SpeechSynthesisResult> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Reason);
}
