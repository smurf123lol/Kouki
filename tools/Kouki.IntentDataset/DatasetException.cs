namespace Kouki.IntentDataset;

/// <summary>
/// Ошибка сборки датасета, при которой набор собирать нельзя: файла нет, строка битая,
/// метка не входит в классы модели. CLI печатает текст как есть и выходит с кодом 2.
/// </summary>
public sealed class DatasetException : Exception
{
    public DatasetException(string message) : base(message)
    {
    }

    public DatasetException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
