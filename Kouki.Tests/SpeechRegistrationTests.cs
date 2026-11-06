using Kouki.Shimi.ServiceAbstractions;
using Kouki.Un;
using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Tests;

/// <summary>
/// Регистрация слуха и голоса: заглушки должны быть всегда, а sherpa — заменять
/// их только тогда, когда модель на месте.
/// </summary>
/// <remarks>
/// Проверяем инвариант, из-за которого /api/speech падал на DI: путь «распознавание
/// есть, каталога голоса нет» оставлял хост без <see cref="ISpeechSynthesizer"/>,
/// потому что заглушки регистрировались только в ветке «моделей нет вообще».
/// </remarks>
public sealed class SpeechRegistrationTests
{
    /// <summary>Без моделей обе службы — заглушки, и хост не падает.</summary>
    [Fact]
    public void AddUn_registers_both_stubs()
    {
        var services = new ServiceCollection();
        services.AddUn();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<UnavailableSpeechTranscriber>(provider.GetRequiredService<ISpeechTranscriber>());
        Assert.IsType<UnavailableSpeechSynthesizer>(provider.GetRequiredService<ISpeechSynthesizer>());
    }

    /// <summary>Заглушка умеет объяснять конкретную причину, а не только «сборка без sherpa».</summary>
    [Fact]
    public async Task Unavailable_synthesizer_explains_custom_reason()
    {
        const string reason = "голос по умолчанию 'ru_RU-irina-medium' не найден в '/tmp/voice'";
        var synthesizer = new UnavailableSpeechSynthesizer(reason);

        Assert.False(synthesizer.IsConfigured);
        Assert.Equal(reason, synthesizer.UnavailableReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => synthesizer.SynthesizeAsync("привет"));
    }

#if KOUKI_SHERPA
    /// <summary>
    /// Распознавание подключено, голос — нет: трансcriber заменяется настоящим,
    /// синтезатор остаётся заглушкой (а не отсутствует вовсе).
    /// </summary>
    [Fact]
    public void Sherpa_transcriber_advances_but_missing_voice_keeps_stub_synthesizer()
    {
        var services = new ServiceCollection();
        services.AddUn();
        Kouki.Un.Sherpa.ServiceCollectionExtensions.AddUnSherpa(services);

        // Транскрибер проверяем по дескриптору: его конструктор трогает нативные
        // библиотеки, а тесту нужен только факт подмены заглушки.
        // Заглушка трансcriber'а удалена, вместо неё — реализация sherpa.
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(ISpeechTranscriber) &&
            descriptor.ImplementationType == typeof(UnavailableSpeechTranscriber));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ISpeechTranscriber));

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType<UnavailableSpeechSynthesizer>(provider.GetRequiredService<ISpeechSynthesizer>());
    }
#endif
}
