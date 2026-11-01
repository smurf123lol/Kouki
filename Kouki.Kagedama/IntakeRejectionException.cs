namespace Kouki.Kagedama;

/// <summary>
/// Отказ приёма, у которого есть код для хоста: HTTP и MCP отвечают на него 400, а не 500.
/// </summary>
/// <remarks>
/// Хосты сначала проверяют фразу правилами <see cref="IntakeRequestPolicy"/> и сами отдают
/// понятный отказ. Исключение — защита для тех, кто зовёт <see cref="TaskIntakeService"/>
/// напрямую, без границы: фраза, состоящая из одних тегов, не должна превращаться в ошибку сервера.
/// </remarks>
public sealed class IntakeRejectionException : Exception
{
    /// <summary>
    /// Создаёт отказ с кодом.
    /// </summary>
    /// <param name="code">Код отказа, см. <see cref="IntakeRequestPolicy"/>.</param>
    /// <param name="message">Текст для человека.</param>
    public IntakeRejectionException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>Код отказа: стабильное значение для программ, не для человека.</summary>
    public string Code { get; }
}
