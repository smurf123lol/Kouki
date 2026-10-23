using Kouki.Kagedama;
using Kouki.Kagedama.Commands;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using Kouki.Mugura.DependencyInjection;
using Kouki.Shimi.Context;
using Kouki.Shimi.DependencyInjection;
using Kouki.Shimi.ServiceAbstractions;
using Kouki.Ubusuna.Commands;
using Kouki.Yasabi;
using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Tests;

/// <summary>
/// Голосовой приём: WAV → текст → та же дорога, что у обычной фразы.
/// </summary>
public sealed class VoiceIntakeTests : IDisposable
{
    private readonly string _directory;

    public VoiceIntakeTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kouki-voice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог мог остаться занятым — не повод ронять тест.
        }
    }

    [Fact]
    public void Wav_reader_accepts_station_format()
    {
        byte[] wav = TestWav.Create();

        Assert.True(WavReader.TryParse(wav, out WavAudio? audio, out string? error), error);
        Assert.NotNull(audio);
        Assert.Equal(16000, audio!.SampleRate);
        Assert.Equal(1, audio.Channels);
        Assert.Equal(16, audio.BitsPerSample);
        Assert.Equal(0.5d, audio.Duration.TotalSeconds, precision: 2);
    }

    [Fact]
    public void Wav_reader_explains_rejected_sample_rate()
    {
        byte[] wav = TestWav.Create(sampleRate: 44100);

        Assert.False(WavReader.TryParse(wav, out WavAudio? audio, out string? error));
        Assert.Null(audio);
        Assert.Contains("16000", error);
    }

    [Fact]
    public void Wav_reader_rejects_foreign_bytes()
    {
        Assert.False(WavReader.TryParse(new byte[] { 1, 2, 3 }, out _, out string? error));
        Assert.Contains("RIFF", error);
    }

    [Fact]
    public async Task Voice_intake_creates_task_and_stores_audio()
    {
        using VoiceFixture fixture = BuildVoiceFixture("запиши задачу купить хлеб", confidence: 0.92);

        VoiceIntakeResult result = await fixture.Service.AcceptAsync(
            TestWav.Create(), "station", AudioFormat.Wav16KiloHertzMono);

        Assert.Null(result.Error);
        Assert.Equal(IntakeOutcomes.Created, result.Intake!.Outcome);
        Assert.NotNull(result.AudioPath);
        Assert.True(File.Exists(result.AudioPath));

        TaskRecord task = Assert.IsType<TaskRecord>(result.Intake.Task);
        Assert.Equal("station", task.Source);
        Assert.Contains("asrConfidence", task.MetaJson);
        Assert.Contains("audioPath", task.MetaJson);

        IReadOnlyList<ClassificationEntry> journal = await fixture.Journal.ListAsync(10, false);
        ClassificationEntry record = Assert.Single(journal);
        Assert.Equal(0.92d, record.AsrConfidence);
        Assert.Equal(result.AudioPath, record.AudioPath);
    }

    [Fact]
    public async Task Low_asr_confidence_does_not_force_review()
    {
        // sherpa-onnx вероятность не отдаёт, и порога на неё нет намеренно:
        // уверенная фраза исполняется, а уверенность ASR остаётся только метаданными.
        using VoiceFixture fixture = BuildVoiceFixture("запиши задачу позвонить маме", confidence: 0.2);

        VoiceIntakeResult result = await fixture.Service.AcceptAsync(
            TestWav.Create(), "station", AudioFormat.Wav16KiloHertzMono);

        Assert.Equal(IntakeOutcomes.Created, result.Intake!.Outcome);
        Assert.Contains("asrConfidence", result.Intake.Task!.MetaJson);
    }

    [Fact]
    public async Task Unrecognized_speech_returns_readable_error()
    {
        using VoiceFixture fixture = BuildVoiceFixture(string.Empty, confidence: null);

        VoiceIntakeResult result = await fixture.Service.AcceptAsync(
            TestWav.Create(), "station", AudioFormat.Wav16KiloHertzMono);

        Assert.Null(result.Intake);
        Assert.Equal("Речь не распознана.", result.Error);
    }

    private VoiceFixture BuildVoiceFixture(string text, double? confidence)
    {
        var services = new ServiceCollection();
        string databasePath = Path.Combine(_directory, "kouki.sqlite");
        DatabaseInitializer.Initialize(databasePath);

        services.AddKagedamaData(databasePath);
        services.AddMugura(
            typeof(PingHandler).Assembly,
            typeof(ListTasksHandler).Assembly);
        services.AddShimiContext();
        services.AddYasabi();
        services.AddSingleton(new IntakeOptions());
        services.AddSingleton<ISpeechTranscriber>(new FakeTranscriber(text, confidence));
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<VoiceIntakeService>>(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VoiceIntakeService>.Instance);
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<FileAudioStore>>(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FileAudioStore>.Instance);
        services.AddSingleton<TaskIntakeService>();
        services.AddSingleton(sp => new VoiceIntakeService(
            sp.GetRequiredService<ISpeechTranscriber>(),
            sp.GetRequiredService<TaskIntakeService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<VoiceIntakeService>>(),
            new FileAudioStore(
                Path.Combine(_directory, "audio"),
                sp.GetRequiredService<IClassificationJournal>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileAudioStore>>())));

        ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<IContextRegistry>().SetDefaultContext(
            new BaseExecutionContext(
                "test",
                new StubFileSystem(),
                new StubTerminal(),
                new InMemoryStateStore()));

        return new VoiceFixture(provider);
    }

    private sealed class VoiceFixture : IDisposable
    {
        public VoiceFixture(ServiceProvider provider)
        {
            Provider = provider;
            Service = provider.GetRequiredService<VoiceIntakeService>();
            Journal = provider.GetRequiredService<IClassificationJournal>();
        }

        public ServiceProvider Provider { get; }

        public VoiceIntakeService Service { get; }

        public IClassificationJournal Journal { get; }

        public void Dispose() => Provider.Dispose();
    }
}

/// <summary>Подставное распознавание речи: возвращает заданный текст.</summary>
internal sealed class FakeTranscriber : ISpeechTranscriber
{
    private readonly string _text;
    private readonly double? _confidence;

    public FakeTranscriber(string text, double? confidence)
    {
        _text = text;
        _confidence = confidence;
    }

    public Task<TranscriptionResult> TranscribeAsync(Stream audio, AudioFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return Task.FromResult(new TranscriptionResult(_text, _confidence, Array.Empty<TranscriptionSegment>()));
    }
}

/// <summary>Сборка минимального WAV нужного формата.</summary>
internal static class TestWav
{
    public static byte[] Create(int sampleRate = 16000, double seconds = 0.5)
    {
        short channels = 1;
        short bitsPerSample = 16;
        int sampleCount = (int)(sampleRate * (double)seconds);
        int dataLength = sampleCount * channels * (bitsPerSample / 8);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8));
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataLength);

        // Тишина: для приёма важно не содержимое, а формат и длина.
        writer.Write(new byte[dataLength]);

        return stream.ToArray();
    }
}
