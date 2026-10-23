using System.Text.Json.Nodes;

namespace Kouki.Yasabi;

/// <summary>
/// Что имелось в виду и насколько классификатор в этом уверен.
/// </summary>
/// <param name="Intent">Имя интента (совпадает с именем команды Kouki по соглашению).</param>
/// <param name="Score">Уверенность от 0 до 1.</param>
/// <param name="Slots">Слоты, извлечённые из фразы, если классификатор их умеет.</param>
/// <param name="Model">Имя модели или набора правил, давшего ответ.</param>
public sealed record IntentResult(
    string Intent,
    double Score,
    JsonObject? Slots = null,
    string Model = "unknown",
    Guid? JournalId = null)
{
    /// <summary>Интент, который означает «не понял».</summary>
    public const string Unclassified = "unclassified";

    /// <summary>Ответ «не понял», с нулевой уверенностью.</summary>
    /// <param name="model">Имя модели.</param>
    public static IntentResult Unknown(string model) => new(Unclassified, 0d, null, model);

    /// <summary>
    /// Достаточно ли уверенности, чтобы исполнять действие без участия человека.
    /// </summary>
    /// <param name="threshold">Порог уверенности.</param>
    public bool IsConfident(double threshold) => Intent != Unclassified && Score >= threshold;
}
