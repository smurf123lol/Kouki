using Kouki.Kagedama;

namespace Kouki.Tests;

public sealed class TextLanguageTests
{
    [Fact]
    public void Russian_text_is_not_marked()
    {
        Assert.Null(TextLanguage.Detect("Запиши задачу купить хлеб"));
    }

    [Fact]
    public void Latin_phrase_is_marked()
    {
        Assert.Equal(TextLanguage.NonRussian, TextLanguage.Detect("This is a result of the test"));
    }

    [Fact]
    public void Short_technical_inserts_are_not_a_language()
    {
        // Названия вида «Wi-Fi», «GPT-4» в русской фразе не должны переводить запись в «нерусские».
        Assert.Null(TextLanguage.Detect("Проверь Wi-Fi и GPT"));
    }

    [Fact]
    public void Text_without_letters_is_marked_separately()
    {
        Assert.Equal(TextLanguage.NoLetters, TextLanguage.Detect("123 456"));
    }

    [Fact]
    public void Description_keeps_a_short_sample()
    {
        string description = TextLanguage.Describe("Hello, this is a long English sentence about tasks", TextLanguage.NonRussian);

        Assert.StartsWith(TextLanguage.NonRussian, description, StringComparison.Ordinal);
        Assert.Contains("Hello", description, StringComparison.Ordinal);
    }
}
