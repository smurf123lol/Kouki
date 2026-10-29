using Kouki.Kagedama.Contracts;

namespace Kouki.Kotae;

/// <summary>
/// Ответчик, когда модель не подключена: команда <c>ask</c> отвечает заглушкой,
/// но причина видна в <see cref="UnavailableReason"/>.
/// </summary>
/// <remarks>
/// Отдельный класс, а не «нет регистрации»: так <c>AskHandler</c> всегда получает
/// <see cref="IAnswerer"/>, и в диагностике видно, что именно не настроено.
/// </remarks>
public sealed class DisabledAnswerer : IAnswerer
{
    /// <summary>Создаёт заглушку с объяснением.</summary>
    public DisabledAnswerer(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        UnavailableReason = reason;
    }

    /// <inheritdoc />
    public bool IsConfigured => false;

    /// <inheritdoc />
    public string? UnavailableReason { get; }

    /// <inheritdoc />
    public string Signature => "none";

    /// <inheritdoc />
    public Task<Answer> AnswerAsync(string question, CancellationToken cancellationToken = default)
    {
        throw new AnswererException(UnavailableReason!);
    }
}
