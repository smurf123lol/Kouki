using Kouki.Shimi.ServiceAbstractions;
using Kouki.Un;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kouki.Kagedama.Api;

/// <summary>
/// Регистрация слуха и голоса в хосте API: заглушки, sherpa и решение
/// «поднимать ли модели», вынесенное из <c>Program.cs</c>, чтобы его можно было тестировать.
/// </summary>
/// <remarks>
/// Инвариант, из-за которого это выделено (T108): заглушки <see cref="AddUn()"/>
/// регистрируются всегда, а sherpa заменяет их только при валидных моделях. Раньше решение
/// принималось прямо в top-level <c>Program.cs</c>, то есть тестами не покрывалось: удаление
/// строк, снимающих заглушку синтезатора, оставляло все тесты зелёными.
/// </remarks>
public static class SpeechRegistration
{
    /// <summary>Настройки слуха и голоса, как они пришли из конфигурации.</summary>
    /// <param name="ModelDirectory">Каталог модели ASR.</param>
    /// <param name="Language">Язык распознавания.</param>
    /// <param name="VoiceDirectory">Каталог голосов синтеза.</param>
    /// <param name="Voice">Имя голоса по умолчанию.</param>
    /// <param name="MaxLoadedVoices">Сколько голосов держать загруженными.</param>
    public sealed record SpeechSettings(
        string? ModelDirectory,
        string? Language,
        string? VoiceDirectory,
        string Voice,
        int MaxLoadedVoices);

    /// <summary>Итог регистрации: причины отключения для логов и ответов 503.</summary>
    /// <param name="SpeechFallbackReason">Почему распознавание осталось на заглушке.</param>
    /// <param name="VoiceFallbackReason">Почему синтез остался на заглушке.</param>
    public sealed record Result(string? SpeechFallbackReason, string? VoiceFallbackReason);

    /// <summary>
    /// Регистрирует настоящие службы либо заглушки с понятной причиной.
    /// </summary>
    /// <param name="services">Коллекция сервисов хоста.</param>
    /// <param name="settings">Каталоги моделей и голосовые настройки.</param>
    /// <returns>Причины отключения — для предупреждений в лог и ответов 503.</returns>
    /// <remarks>
    /// Четыре ветки: сборка без sherpa; sherpa с валидными моделью и голосом; sherpa с моделью
    /// без голоса; sherpa без модели. Все четыре покрыты тестами.
    /// </remarks>
    public static Result AddSpeech(IServiceCollection services, SpeechSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        string? speechFallbackReason = null;
        string? voiceFallbackReason = null;

        // Заглушки нужны всегда: это фолбэк, когда моделей нет, а также то, что sherpa
        // заменяет, если модели на месте. Без них путь «распознавание есть, голоса нет»
        // оставлял /api/speech без ISpeechSynthesizer — падение на DI вместо 503.
        services.AddUn();
#if KOUKI_SHERPA
        if (Kouki.Un.SpeechModelCheck.TryValidate(settings.ModelDirectory, out string? modelProblem))
        {
            Kouki.Un.Sherpa.ServiceCollectionExtensions.AddUnSherpa(
                services,
                sherpa =>
                {
                    sherpa.ModelDirectory = settings.ModelDirectory;
                    sherpa.Language = settings.Language;
                });

            // Голос — та же библиотека: без каталога голоса озвучивание просто выключается,
            // распознавание при этом продолжает работать.
            string? defaultVoiceDir = Kouki.Un.SpeechVoiceResolver.ResolveVoiceDirectory(
                settings.VoiceDirectory,
                settings.Voice,
                settings.Voice);

            if (defaultVoiceDir is not null)
            {
                Kouki.Un.Sherpa.ServiceCollectionExtensions.AddUnSherpaVoice(
                    services,
                    voice =>
                    {
                        voice.VoiceDirectory = settings.VoiceDirectory;
                        voice.DefaultVoice = settings.Voice;
                        voice.MaxLoadedVoices = settings.MaxLoadedVoices;
                    });
            }
            else
            {
                voiceFallbackReason =
                    $"голос по умолчанию '{settings.Voice}' не найден в '{settings.VoiceDirectory}'";
                // Заглушка должна объяснять настоящую причину: сборка с sherpa, но без голоса.
                services.RemoveAll<ISpeechSynthesizer>();
                services.AddSingleton<ISpeechSynthesizer>(
                    new UnavailableSpeechSynthesizer(voiceFallbackReason));
            }
        }
        else
        {
            speechFallbackReason = modelProblem;
            // Распознавание не подняли — значит и синтез не регистрировался: причина у него
            // не «сборка без sherpa», а именно модель ASR. Врать в 503 и логе нельзя.
            voiceFallbackReason = $"озвучивание не поднято вместе с распознаванием: {modelProblem}";
            services.RemoveAll<ISpeechSynthesizer>();
            services.AddSingleton<ISpeechSynthesizer>(
                new UnavailableSpeechSynthesizer(voiceFallbackReason));
        }
#endif

        return new Result(speechFallbackReason, voiceFallbackReason);
    }
}
