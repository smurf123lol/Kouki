namespace Kouki.Yasabi;

/// <summary>
/// Понимание текста: превращает фразу в интент и слоты (муси Ясаби).
/// Реализации: правила сейчас, BERT/ONNX или HTTP-сервис позже —
/// подмена не меняет вызывающий код.
/// </summary>
public interface IIntentClassifier
{
    /// <summary>
    /// Классифицирует фразу.
    /// </summary>
    /// <param name="text">Текст запроса.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Интент с уверенностью; при непонимании — <see cref="IntentResult.Unclassified"/>.</returns>
    Task<IntentResult> ClassifyAsync(string text, CancellationToken cancellationToken = default);
}
