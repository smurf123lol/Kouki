using Kouki.Kagedama.Api;
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

    [Fact]
    public void Speech_with_valid_model_and_voice_registers_real_services()
    {
#if KOUKI_SHERPA
        using var dirs = new TempServiceDirs(createModel: true, createVoice: true);
        var services = new ServiceCollection();

        SpeechRegistration.Result result = SpeechRegistration.AddSpeech(
            services,
            new SpeechRegistration.SpeechSettings(
                dirs.ModelDirectory,
                "ru",
                dirs.VoiceRoot,
                TempServiceDirs.Voice,
                MaxLoadedVoices: 2));

        Assert.Null(result.SpeechFallbackReason);
        Assert.Null(result.VoiceFallbackReason);
        AssertNoStub<ISpeechTranscriber, UnavailableSpeechTranscriber>(services);
        AssertNoStub<ISpeechSynthesizer, UnavailableSpeechSynthesizer>(services);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ISpeechTranscriber));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ISpeechSynthesizer));
#else
        // Без пакета sherpa настоящие службы не регистрируются: обе — заглушки.
        using var dirs = new TempServiceDirs(createModel: false, createVoice: false);
        var services = new ServiceCollection();
        SpeechRegistration.Result result = SpeechRegistration.AddSpeech(
            services,
            new SpeechRegistration.SpeechSettings(
                dirs.ModelDirectory, "ru", dirs.VoiceRoot, TempServiceDirs.Voice, MaxLoadedVoices: 2));

        Assert.Null(result.SpeechFallbackReason);
        Assert.Null(result.VoiceFallbackReason);
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType<UnavailableSpeechTranscriber>(provider.GetRequiredService<ISpeechTranscriber>());
        Assert.IsType<UnavailableSpeechSynthesizer>(provider.GetRequiredService<ISpeechSynthesizer>());
#endif
    }

#if KOUKI_SHERPA
    [Fact]
    public void Speech_with_valid_model_and_missing_voice_keeps_stub_synthesizer_with_reason()
    {
        using var dirs = new TempServiceDirs(createModel: true, createVoice: false);
        var services = new ServiceCollection();

        SpeechRegistration.Result result = SpeechRegistration.AddSpeech(
            services,
            new SpeechRegistration.SpeechSettings(
                dirs.ModelDirectory, "ru", dirs.VoiceRoot, TempServiceDirs.Voice, MaxLoadedVoices: 2));

        Assert.Null(result.SpeechFallbackReason);
        Assert.NotNull(result.VoiceFallbackReason);
        Assert.Contains("не найден", result.VoiceFallbackReason, StringComparison.Ordinal);

        // Транскрибер настоящий (заглушки нет), синтезатор — заглушка с причиной.
        AssertNoStub<ISpeechTranscriber, UnavailableSpeechTranscriber>(services);
        using ServiceProvider provider = services.BuildServiceProvider();
        var synthesizer = Assert.IsType<UnavailableSpeechSynthesizer>(
            provider.GetRequiredService<ISpeechSynthesizer>());
        Assert.Equal(result.VoiceFallbackReason, synthesizer.UnavailableReason);
    }

    [Fact]
    public void Speech_without_model_registers_both_stubs_and_explains_asr_problem()
    {
        using var dirs = new TempServiceDirs(createModel: false, createVoice: true);
        var services = new ServiceCollection();

        // Каталога модели нет вовсе — самый частый случай свежей установки.
        SpeechRegistration.Result result = SpeechRegistration.AddSpeech(
            services,
            new SpeechRegistration.SpeechSettings(
                Path.Combine(dirs.Root, "missing-model"),
                "ru",
                dirs.VoiceRoot,
                TempServiceDirs.Voice,
                MaxLoadedVoices: 2));

        Assert.NotNull(result.SpeechFallbackReason);
        Assert.Contains("нет каталога моделей", result.SpeechFallbackReason, StringComparison.Ordinal);
        // Причина 503 — модель ASR, а не «сборка без sherpa-onnx» (m1 ревью).
        Assert.NotNull(result.VoiceFallbackReason);
        Assert.Contains("вместе с распознаванием", result.VoiceFallbackReason, StringComparison.Ordinal);

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType<UnavailableSpeechTranscriber>(provider.GetRequiredService<ISpeechTranscriber>());
        var synthesizer = Assert.IsType<UnavailableSpeechSynthesizer>(
            provider.GetRequiredService<ISpeechSynthesizer>());
        Assert.Equal(result.VoiceFallbackReason, synthesizer.UnavailableReason);
    }
#endif

    private static void AssertNoStub<TService, TStub>(IServiceCollection services)
        where TStub : class
    {
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(TService) &&
            descriptor.ImplementationType == typeof(TStub));
    }

    /// <summary>Временные каталоги модели ASR и голоса: проверка валидна и на пустых файлах.</summary>
    private sealed class TempServiceDirs : IDisposable
    {
        public const string Voice = "ru_RU-irina-medium";

        public TempServiceDirs(bool createModel, bool createVoice)
        {
            Root = Directory.CreateTempSubdirectory("kouki-speech-").FullName;
            ModelDirectory = Path.Combine(Root, "asr-model");
            VoiceRoot = Path.Combine(Root, "voices");
            Directory.CreateDirectory(ModelDirectory);
            Directory.CreateDirectory(VoiceRoot);

            if (createModel)
            {
                File.WriteAllText(Path.Combine(ModelDirectory, "tokens.txt"), string.Empty);
                File.WriteAllText(Path.Combine(ModelDirectory, "encoder-epoch.onnx"), string.Empty);
                File.WriteAllText(Path.Combine(ModelDirectory, "decoder-epoch.onnx"), string.Empty);
            }

            if (createVoice)
            {
                string voiceDir = Path.Combine(VoiceRoot, "vits-piper-" + Voice);
                Directory.CreateDirectory(voiceDir);
                File.WriteAllText(Path.Combine(voiceDir, "tokens.txt"), string.Empty);
                File.WriteAllText(Path.Combine(voiceDir, Voice + ".onnx"), string.Empty);
            }
        }

        public string Root { get; }

        public string ModelDirectory { get; }

        public string VoiceRoot { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Временный каталог: не мешает результату теста.
            }
        }
    }
}
