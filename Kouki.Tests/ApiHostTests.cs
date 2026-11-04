using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Kouki.Kagedama;
using TaskFactory = Kouki.Kagedama.TaskFactory;
using Kouki.Kagedama.Api;
using Kouki.Kagedama.Api.Api;
using Kouki.Kagedama.Api.Auth;
using Kouki.Kagedama.Commands;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Hosting;
using Kouki.Mugura.Dispatcher;
using Kouki.Shimi.ServiceAbstractions;
using Kouki.Un;
using Kouki.Ubusuna.DependencyInjection;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kouki.Tests;

/// <summary>
/// Граница HTTP целиком: настоящий Kestrel-хост на временной базе и запросы по сети.
/// </summary>
/// <remarks>
/// Почему не <c>WebApplicationFactory</c>/<c>TestServer</c>: пакета <c>Microsoft.AspNetCore.Mvc.Testing</c>
/// нет в офлайн-кэше NuGet (кэш read-only), а версии пакетов централизованы в
/// <c>Directory.Packages.props</c> — добавить его в тестовый проект нельзя. Поэтому хост
/// собирается теми же точками композиции, что боевой <c>Program.cs</c>:
/// <c>AddKoukiCore</c> → <c>InitializeKouki</c> → <see cref="TokenAuthMiddleware"/> →
/// <see cref="ApiEndpoints.MapKoukiApi"/>. Порядок тот же, поэтому проверяются именно
/// эндпоинты и команда под ними, а не только сервисы.
/// Порт 0 — свободный порт от ОС, чтобы параллельные тесты не мешали друг другу.
/// </remarks>
public sealed class ApiHostFixture : IAsyncLifetime
{
    /// <summary>Токен тестового хоста: без него API закрыт (fail-closed).</summary>
    public const string Token = "host-boundary-token";

    private WebApplication? _app;
    private string _directory = string.Empty;

    /// <summary>HTTP-клиент с bearer-токеном.</summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>Адрес запущенного хоста — для клиентов без токена.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>Путь к временной базе теста.</summary>
    public string DatabasePath { get; private set; } = string.Empty;

    /// <summary>Каталог аудио теста: тот же, что у <see cref="FileAudioStore"/> и <c>/api/audio</c>.</summary>
    public string AudioDirectory { get; private set; } = string.Empty;

    /// <summary>Контейнер хоста: тесты кладут задачи прямо в хранилище.</summary>
    public IServiceProvider Services => _app!.Services;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kouki-api-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory, "kouki.sqlite");
        AudioDirectory = Path.Combine(_directory, "audio");
        Directory.CreateDirectory(AudioDirectory);

        var serverOptions = new KoukiServerOptions
        {
            DatabasePath = DatabasePath,
            AudioDirectory = AudioDirectory,
            Token = Token,
            MaxTextLength = 2000,
            TagMode = TagModes.Extract,
        };

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Test",
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(serverOptions);
        builder.Services.AddSingleton<IOptions<KoukiServerOptions>>(Options.Create(serverOptions));

        // Тестовый хост повторяет боевую регистрацию: cascade — только с confirm=true,
        // detach — без подтверждения, остальные опасные команды запрещены.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<ICommandConfirmation, ApiCommandConfirmation>();

        builder.Services.AddKoukiCore(new KoukiHostSettings
        {
            DatabasePath = DatabasePath,
            ConfidenceThreshold = 0.75,
            DefaultSource = TaskSources.Api,
            TagMode = TagModes.Extract,
        });

        // Консольные адаптеры нужны InitializeKouki: контекст по умолчанию берёт из них
        // IFileSystem и ITerminal — без регистрации контекст не собрать.
        builder.Services.AddUbusuna();

        // Метаданные маршрутов резолвятся целиком при первом запросе, поэтому все типы
        // параметров эндпоинтов обязаны быть в контейнере — даже те, чьи маршруты тест
        // не зовёт.
        builder.Services.AddUn();
        builder.Services.AddSingleton<ISpeechTranscriber, TestTranscriber>();
        builder.Services.AddSingleton<VoiceIntakeService>(sp => new VoiceIntakeService(
            sp.GetRequiredService<ISpeechTranscriber>(),
            sp.GetRequiredService<TaskIntakeService>(),
            sp.GetRequiredService<ILogger<VoiceIntakeService>>()));

        _app = builder.Build();
        KoukiHosting.InitializeKouki(_app.Services, "test");
        _app.UseMiddleware<TokenAuthMiddleware>();
        _app.MapKoukiApi();

        await _app.StartAsync();

        IServer server = _app.Services.GetRequiredService<IServer>();
        IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel не сообщил адрес запуска.");
        string address = addresses.Addresses.First();

        BaseAddress = new Uri(address);
        Client = new HttpClient { BaseAddress = BaseAddress };
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        Client?.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Временный каталог за собой уберёт система: тест уже не от этого зависит.
        }
    }

    private sealed class TestTranscriber : ISpeechTranscriber
    {
        public Task<TranscriptionResult> TranscribeAsync(Stream audio, AudioFormat format, CancellationToken cancellationToken = default)
            => Task.FromResult(TranscriptionResult.Empty);
    }

    /// <summary>Кладёт задачу с тегом прямо в хранилище — без HTTP, чтобы тест про фильтр
    /// не зависел от приёма фразы.</summary>
    /// <param name="title">Текст задачи.</param>
    /// <param name="tag">Тег в канонической форме.</param>
    /// <param name="status">Статус задачи.</param>
    /// <returns>Созданная запись.</returns>
    public async Task<TaskRecord> SeedTaskAsync(
        string title,
        string tag,
        string status = TaskStatuses.New)
    {
        ITaskStore store = Services.GetRequiredService<ITaskStore>();
        ITagStore tags = Services.GetRequiredService<ITagStore>();

        TaskRecord record = TaskFactory.Create(title, TaskSources.Api, status);
        await store.AddAsync(record);
        await tags.AddAsync(record.Id, new[] { tag }, TagSources.Text);
        return record;
    }

    /// <summary>Кладёт запись журнала — чтобы тест фильтра и аудио не зависел от приёма фразы.</summary>
    /// <param name="text">Текст записи.</param>
    /// <param name="taskId">Задача, к которой привязать запись; <c>null</c> — не привязывать.</param>
    /// <param name="audioPath">Путь к аудио; <c>null</c> — записи без аудио.</param>
    /// <returns>Идентификатор записи журнала.</returns>
    public async Task<Guid> RecordClassificationAsync(string text, Guid? taskId = null, string? audioPath = null)
    {
        IClassificationJournal journal = Services.GetRequiredService<IClassificationJournal>();

        Guid id = await journal.RecordAsync(new ClassificationRecord(
            TaskSources.Api,
            "text",
            text,
            "unclassified",
            0,
            "rules-v1",
            Confident: false,
            Error: null));

        if (taskId is not null)
        {
            await journal.LinkToTaskAsync(id, taskId.Value);
        }

        if (audioPath is not null)
        {
            await journal.AnnotateAudioAsync(id, null, audioPath);
        }

        return id;
    }

    /// <summary>Читает JSON-массив задач из ответа <c>GET /api/tasks</c>.</summary>
    /// <param name="response">Ответ хоста.</param>
    /// <returns>Массив задач.</returns>
    public static async Task<JsonArray> ReadTasksAsync(HttpResponseMessage response)
    {
        JsonNode? node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        JsonArray tasks = Assert.IsType<JsonArray>(node);
        return tasks;
    }
}

/// <summary>
/// Проверки границы "HTTP → команда → SQLite" на реальном хосте: теги в приёме и фильтре.
/// </summary>
public sealed class ApiHostTests : IClassFixture<ApiHostFixture>
{
    private readonly ApiHostFixture _host;

    /// <summary>Принимает общий хост класса: база одна на все тесты, теги у тестов разные.</summary>
    /// <param name="host">Хост с временной базой.</param>
    public ApiHostTests(ApiHostFixture host)
    {
        _host = host;
    }

    [Fact]
    public async Task Intake_of_tags_only_phrase_is_400_with_code()
    {
        using var response = await _host.Client.PostAsync(
            "/api/intake",
            JsonContent.Create(new { text = "#толькотег", source = TaskSources.Api }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        JsonObject body = Assert.IsType<JsonObject>(
            JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        Assert.Equal(IntakeRequestPolicy.TagsOnlyText, body["code"]!.GetValue<string>());
        Assert.Equal(IntakeRequestPolicy.TagsOnlyMessage, body["error"]!.GetValue<string>());

        // После отказа задачи не появилось: отбраковка идёт до приёма.
        using var filtered = await _host.Client.GetAsync("/api/tasks?tag=" + Uri.EscapeDataString("толькотег"));
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        Assert.Empty(await ApiHostFixture.ReadTasksAsync(filtered));
    }

    [Fact]
    public async Task Tasks_are_filtered_by_tag_and_empty_tag_is_400()
    {
        TaskRecord wanted = await _host.SeedTaskAsync("ответить на письмо", "фильтр");

        // «#» и регистр снимает нормализация: «ФИЛЬТР» находит канонический «фильтр».
        using var filtered = await _host.Client.GetAsync("/api/tasks?tag=" + Uri.EscapeDataString("ФИЛЬТР"));
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);

        JsonArray tasks = await ApiHostFixture.ReadTasksAsync(filtered);
        JsonObject only = Assert.IsType<JsonObject>(Assert.Single(tasks));
        Assert.Equal(wanted.Id.ToString(), only["id"]!.GetValue<string>());
        Assert.Equal(
            new[] { "фильтр" },
            only["tags"]!.AsArray().Select(node => node!.GetValue<string>()));

        // Пустое значение параметра — ошибка клиента, а не «без фильтра».
        using var empty = await _host.Client.GetAsync("/api/tasks?tag=");
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        JsonObject emptyBody = Assert.IsType<JsonObject>(
            JsonNode.Parse(await empty.Content.ReadAsStringAsync()));
        Assert.Equal(ListTasksHandler.EmptyTagError, emptyBody["error"]!.GetValue<string>());

        // После нормализации пусто: решётка без слова — тот же 400.
        using var hashOnly = await _host.Client.GetAsync("/api/tasks?tag=%23");
        Assert.Equal(HttpStatusCode.BadRequest, hashOnly.StatusCode);

        // Неизвестный тег — штатный пустой список, не ошибка.
        using var unknown = await _host.Client.GetAsync("/api/tasks?tag=" + Uri.EscapeDataString("нет-такого"));
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Empty(await ApiHostFixture.ReadTasksAsync(unknown));
    }

    [Fact]
    public async Task Tag_status_and_limit_are_applied_together_over_http()
    {
        TaskRecord wanted = await _host.SeedTaskAsync("ответить на письмо", "лимит", TaskStatuses.New);
        await _host.SeedTaskAsync("старая задача раз", "лимит", TaskStatuses.Done);
        await _host.SeedTaskAsync("старая задача два", "лимит", TaskStatuses.Done);

        // Регрессия: LIMIT в SQL считался до фильтра статуса, и новые задачи вытеснялись
        // выполненными — при limit=2 ответ был пустым.
        using var byStatus = await _host.Client.GetAsync("/api/tasks?tag=лимит&status=new&limit=2");
        Assert.Equal(HttpStatusCode.OK, byStatus.StatusCode);

        JsonArray newTasks = await ApiHostFixture.ReadTasksAsync(byStatus);
        JsonObject only = Assert.IsType<JsonObject>(Assert.Single(newTasks));
        Assert.Equal(wanted.Id.ToString(), only["id"]!.GetValue<string>());
        Assert.Equal(TaskStatuses.New, only["status"]!.GetValue<string>());

        // Лимит по-прежнему режет список: без статуса — ровно две задачи.
        using var limited = await _host.Client.GetAsync("/api/tasks?tag=лимит&limit=2");
        Assert.Equal(2, (await ApiHostFixture.ReadTasksAsync(limited)).Count);
    }

    [Fact]
    public async Task Intake_stores_text_tag_and_it_is_visible_through_filter()
    {
        using var response = await _host.Client.PostAsync(
            "/api/intake",
            JsonContent.Create(new { text = "запиши задачу купить хлеб #магазин", source = TaskSources.Api }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonObject body = Assert.IsType<JsonObject>(
            JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        JsonObject task = Assert.IsType<JsonObject>(body["task"]);
        string id = task["id"]!.GetValue<string>();
        Assert.Equal(
            new[] { "магазин" },
            task["tags"]!.AsArray().Select(node => node!.GetValue<string>()));

        using var filtered = await _host.Client.GetAsync("/api/tasks?tag=" + Uri.EscapeDataString("магазин"));
        JsonArray tasks = await ApiHostFixture.ReadTasksAsync(filtered);
        JsonObject only = Assert.IsType<JsonObject>(Assert.Single(tasks));
        Assert.Equal(id, only["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Classifications_are_filtered_by_task_id()
    {
        TaskRecord task = await _host.SeedTaskAsync("фраза с задачей", "журнал");
        Guid linked = await _host.RecordClassificationAsync("купить хлеб", task.Id);
        await _host.RecordClassificationAsync("чужая фраза");

        using var response = await _host.Client.GetAsync($"/api/classifications?taskId={task.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonArray entries = Assert.IsType<JsonArray>(
            JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        JsonObject entry = Assert.IsType<JsonObject>(Assert.Single(entries));

        Assert.Equal(linked.ToString(), entry["id"]!.GetValue<string>());
        Assert.Equal(task.Id.ToString(), entry["taskId"]!.GetValue<string>());
        Assert.False(entry["hasAudio"]!.GetValue<bool>());

        // Путь файловой системы наружу не отдаём: только флаг hasAudio.
        Assert.Null(entry["audioPath"]);
    }

    [Fact]
    public async Task Classifications_with_invalid_task_id_are_400()
    {
        // Некорректный GUID отбивает ASP.NET Core ещё до роутера: команда его не видит.
        using var response = await _host.Client.GetAsync("/api/classifications?taskId=not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Audio_is_returned_with_cache_header_and_matching_bytes()
    {
        byte[] wav = WavBytes();
        string path = Path.Combine(_host.AudioDirectory, "voice", "20261102", "record.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, wav);

        Guid id = await _host.RecordClassificationAsync("голосовая фраза", audioPath: path);

        using var response = await _host.Client.GetAsync($"/api/audio/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromHours(1), response.Headers.CacheControl?.MaxAge);
        Assert.Equal(wav, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Audio_without_path_or_record_is_404()
    {
        Guid withoutAudio = await _host.RecordClassificationAsync("без аудио");

        using var noPath = await _host.Client.GetAsync($"/api/audio/{withoutAudio}");
        Assert.Equal(HttpStatusCode.NotFound, noPath.StatusCode);

        JsonObject payload = Assert.IsType<JsonObject>(
            JsonNode.Parse(await noPath.Content.ReadAsStringAsync()));
        Assert.Equal("Записи нет.", payload["error"]!.GetValue<string>());

        using var missing = await _host.Client.GetAsync($"/api/audio/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Audio_path_outside_audio_directory_is_not_served()
    {
        // В базе может оказаться путь с «..»: файл существует, но лежит вне каталога аудио.
        string secretPath = Path.GetFullPath(Path.Combine(_host.AudioDirectory, "..", "secret.wav"));
        byte[] secret = { 7, 7, 7 };
        await File.WriteAllBytesAsync(secretPath, secret);

        Guid id = await _host.RecordClassificationAsync(
            "подставной путь",
            audioPath: Path.Combine(_host.AudioDirectory, "..", "secret.wav"));

        using var response = await _host.Client.GetAsync($"/api/audio/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(secret, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Audio_symlink_pointing_outside_audio_directory_is_404()
    {
        string secretPath = Path.GetFullPath(Path.Combine(_host.AudioDirectory, "..", "symlink_secret.wav"));
        byte[] secret = { 42, 42, 42 };
        await File.WriteAllBytesAsync(secretPath, secret);

        string symlinkPath = Path.Combine(_host.AudioDirectory, "symlink_leak.wav");
        try
        {
            if (File.Exists(symlinkPath)) File.Delete(symlinkPath);
            File.CreateSymbolicLink(symlinkPath, secretPath);
        }
        catch (Exception)
        {
            // На Windows без прав разработчика создание symlink может требовать SeCreateSymbolicLinkPrivilege.
            // На Linux/сервере выполняется всегда.
            return;
        }

        Guid id = await _host.RecordClassificationAsync(
            "symlink атака",
            audioPath: symlinkPath);

        using var response = await _host.Client.GetAsync($"/api/audio/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(secret, await response.Content.ReadAsByteArrayAsync());

        JsonObject payload = Assert.IsType<JsonObject>(
            JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        Assert.Equal("Записи нет.", payload["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Audio_deleted_file_returns_404_not_500()
    {
        string missingPath = Path.Combine(_host.AudioDirectory, "voice", "20261102", "toctou_deleted.wav");
        // Файла нет на диске (эмулирует удаление retention-сервисом)
        if (File.Exists(missingPath)) File.Delete(missingPath);

        Guid id = await _host.RecordClassificationAsync(
            "удалённый файл",
            audioPath: missingPath);

        using var response = await _host.Client.GetAsync($"/api/audio/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        JsonObject payload = Assert.IsType<JsonObject>(
            JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        Assert.Equal("Записи нет.", payload["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Audio_relative_or_invalid_path_in_db_returns_404()
    {
        Guid id = await _host.RecordClassificationAsync(
            "относительный путь",
            audioPath: "relative/path/record.wav");

        using var response = await _host.Client.GetAsync($"/api/audio/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        JsonObject payload = Assert.IsType<JsonObject>(
            JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        Assert.Equal("Записи нет.", payload["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Api_requires_bearer_token()
    {
        using var anonymous = new HttpClient { BaseAddress = _host.BaseAddress };
        using var response = await anonymous.GetAsync("/api/tasks");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }


    [Fact]
    public async Task Post_tasks_creates_subtask_in_parent_tree()
    {
        TaskRecord parent = await _host.SeedTaskAsync("родитель для HTTP", "http-подзадача");

        using var response = await _host.Client.PostAsync(
            "/api/tasks",
            JsonContent.Create(new { text = "подзадача с доски", parentId = parent.Id }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonObject child = Assert.IsType<JsonObject>(
            JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        string childId = child["id"]!.GetValue<string>();
        Assert.Equal(parent.Id.ToString(), child["parentId"]!.GetValue<string>());
        Assert.Equal(parent.TreeId.ToString(), child["treeId"]!.GetValue<string>());
        Assert.Equal(TaskSources.Dashboard, child["source"]!.GetValue<string>());
        Assert.Equal(0, child["position"]!.GetValue<int>());
        Assert.Equal(0, child["subtasksCount"]!.GetValue<int>());

        using var byParent = await _host.Client.GetAsync($"/api/tasks?parentId={parent.Id}");
        Assert.Equal(HttpStatusCode.OK, byParent.StatusCode);
        JsonObject only = Assert.IsType<JsonObject>(
            Assert.Single(await ApiHostFixture.ReadTasksAsync(byParent)));
        Assert.Equal(childId, only["id"]!.GetValue<string>());

        using var topLevel = await _host.Client.GetAsync("/api/tasks?topLevel=true&limit=500");
        JsonArray roots = await ApiHostFixture.ReadTasksAsync(topLevel);
        Assert.Contains(roots, node => node!["id"]!.GetValue<string>() == parent.Id.ToString());
        Assert.DoesNotContain(roots, node => node!["id"]!.GetValue<string>() == childId);

        using var emptyParent = await _host.Client.GetAsync("/api/tasks?parentId=");
        Assert.Equal(HttpStatusCode.BadRequest, emptyParent.StatusCode);
        JsonObject emptyBody = Assert.IsType<JsonObject>(
            JsonNode.Parse(await emptyParent.Content.ReadAsStringAsync()));
        Assert.Equal(
            "Не указан идентификатор задачи.",
            emptyBody["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Get_and_patch_task_return_subtasks_counts()
    {
        TaskRecord parent = await _host.SeedTaskAsync("родитель со счётчиками", "http-counts");

        Guid firstChild;
        using (var created = await _host.Client.PostAsync(
            "/api/tasks",
            JsonContent.Create(new { text = "первый ребёнок", parentId = parent.Id })))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            firstChild = Guid.Parse(Assert.IsType<JsonObject>(
                JsonNode.Parse(await created.Content.ReadAsStringAsync()))!["id"]!.GetValue<string>());
        }

        using (var created = await _host.Client.PostAsync(
            "/api/tasks",
            JsonContent.Create(new { text = "второй ребёнок", parentId = parent.Id })))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        using (var get = await _host.Client.GetAsync($"/api/tasks/{parent.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            JsonObject json = Assert.IsType<JsonObject>(
                JsonNode.Parse(await get.Content.ReadAsStringAsync()));
            Assert.Equal(2, json["subtasksCount"]!.GetValue<int>());
            Assert.Equal(0, json["subtasksDoneCount"]!.GetValue<int>());
        }

        using (var closed = await _host.Client.PatchAsync(
            $"/api/tasks/{firstChild}",
            JsonContent.Create(new { status = TaskStatuses.Done })))
        {
            Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        }

        using (var get = await _host.Client.GetAsync($"/api/tasks/{parent.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            JsonObject json = Assert.IsType<JsonObject>(
                JsonNode.Parse(await get.Content.ReadAsStringAsync()));
            Assert.Equal(2, json["subtasksCount"]!.GetValue<int>());
            Assert.Equal(1, json["subtasksDoneCount"]!.GetValue<int>());
        }

        // PATCH корня — тот же ответ, что у GET: счётчики не должны обнуляться.
        using (var patched = await _host.Client.PatchAsync(
            $"/api/tasks/{parent.Id}",
            JsonContent.Create(new { title = "родитель со счётчиками (обновлён)" })))
        {
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
            JsonObject json = Assert.IsType<JsonObject>(
                JsonNode.Parse(await patched.Content.ReadAsStringAsync()));
            Assert.Equal(2, json["subtasksCount"]!.GetValue<int>());
            Assert.Equal(1, json["subtasksDoneCount"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task Post_tasks_rejects_empty_text_and_missing_parent()
    {
        using var empty = await _host.Client.PostAsync(
            "/api/tasks",
            JsonContent.Create(new { text = "  " }));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        JsonObject emptyBody = Assert.IsType<JsonObject>(
            JsonNode.Parse(await empty.Content.ReadAsStringAsync()));
        Assert.Equal(
            "Пустая фраза: нечего записывать как задачу.",
            emptyBody["error"]!.GetValue<string>());

        using var orphan = await _host.Client.PostAsync(
            "/api/tasks",
            JsonContent.Create(new { text = "сирота", parentId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.BadRequest, orphan.StatusCode);
        JsonObject orphanBody = Assert.IsType<JsonObject>(
            JsonNode.Parse(await orphan.Content.ReadAsStringAsync()));
        Assert.Equal("Родительская задача не найдена.", orphanBody["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Move_endpoint_changes_status_and_position()
    {
        TaskRecord first = await _host.SeedTaskAsync("первая для move", "http-move");
        TaskRecord second = await _host.SeedTaskAsync("вторая для move", "http-move");

        using var response = await _host.Client.PostAsync(
            $"/api/tasks/{second.Id}/move",
            JsonContent.Create(new { status = TaskStatuses.New, position = 0 }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonObject moved = Assert.IsType<JsonObject>(
            JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        Assert.Equal(second.Id.ToString(), moved["id"]!.GetValue<string>());
        Assert.Equal(TaskStatuses.New, moved["status"]!.GetValue<string>());
        Assert.Equal(0, moved["position"]!.GetValue<int>());
        Assert.Equal(1, (await ReadStoredTask(first.Id))["position"]!.GetValue<int>());

        using var toDone = await _host.Client.PostAsync(
            $"/api/tasks/{second.Id}/move",
            JsonContent.Create(new { status = TaskStatuses.Done, position = 0 }));
        Assert.Equal(HttpStatusCode.OK, toDone.StatusCode);
        JsonObject done = Assert.IsType<JsonObject>(
            JsonNode.Parse(await toDone.Content.ReadAsStringAsync()));
        Assert.Equal(TaskStatuses.Done, done["status"]!.GetValue<string>());

        using var unknownStatus = await _host.Client.PostAsync(
            $"/api/tasks/{second.Id}/move",
            JsonContent.Create(new { status = "когда-нибудь", position = 0 }));
        Assert.Equal(HttpStatusCode.BadRequest, unknownStatus.StatusCode);
        JsonObject unknownBody = Assert.IsType<JsonObject>(
            JsonNode.Parse(await unknownStatus.Content.ReadAsStringAsync()));
        Assert.Equal("Неизвестный статус 'когда-нибудь'.", unknownBody["error"]!.GetValue<string>());

        using var negative = await _host.Client.PostAsync(
            $"/api/tasks/{second.Id}/move",
            JsonContent.Create(new { status = TaskStatuses.New, position = -1 }));
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        JsonObject negativeBody = Assert.IsType<JsonObject>(
            JsonNode.Parse(await negative.Content.ReadAsStringAsync()));
        Assert.Equal("position: целое число от 0.", negativeBody["error"]!.GetValue<string>());

        Guid missing = Guid.NewGuid();
        using var notFound = await _host.Client.PostAsync(
            $"/api/tasks/{missing}/move",
            JsonContent.Create(new { status = TaskStatuses.New, position = 0 }));
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        JsonObject notFoundBody = Assert.IsType<JsonObject>(
            JsonNode.Parse(await notFound.Content.ReadAsStringAsync()));
        Assert.Equal($"Задача {missing} не найдена.", notFoundBody["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Delete_without_confirm_is_400_and_cascade_removes_subtree()
    {
        TaskRecord parent = await _host.SeedTaskAsync("родитель для удаления", "http-delete");

        // Ребёнка создаём через тот же POST, что и дашборд: parent_id ставит команда.
        using var created = await _host.Client.PostAsync(
            "/api/tasks",
            JsonContent.Create(new { text = "ребёнок для удаления", parentId = parent.Id }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonObject childJson = Assert.IsType<JsonObject>(
            JsonNode.Parse(await created.Content.ReadAsStringAsync()));
        string childId = childJson["id"]!.GetValue<string>();

        using var noConfirm = await _host.Client.DeleteAsync($"/api/tasks/{parent.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, noConfirm.StatusCode);
        JsonObject noConfirmBody = Assert.IsType<JsonObject>(
            JsonNode.Parse(await noConfirm.Content.ReadAsStringAsync()));
        Assert.Equal(
            "Удаление с подзадачами требует подтверждения.",
            noConfirmBody["error"]!.GetValue<string>());

        using var confirmed = await _host.Client.DeleteAsync($"/api/tasks/{parent.Id}?confirm=true");
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        JsonObject result = Assert.IsType<JsonObject>(
            JsonNode.Parse(await confirmed.Content.ReadAsStringAsync()));
        Assert.True(result["deleted"]!.GetValue<bool>());
        Assert.Equal("cascade", result["mode"]!.GetValue<string>());
        Assert.Equal(parent.Id.ToString(), result["taskId"]!.GetValue<string>());

        using var gone = await _host.Client.GetAsync($"/api/tasks/{parent.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var childGone = await _host.Client.GetAsync($"/api/tasks/{childId}");
        Assert.Equal(HttpStatusCode.NotFound, childGone.StatusCode);

        using var again = await _host.Client.DeleteAsync($"/api/tasks/{parent.Id}?confirm=true");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        JsonObject againBody = Assert.IsType<JsonObject>(
            JsonNode.Parse(await again.Content.ReadAsStringAsync()));
        Assert.Equal($"Задача {parent.Id} не найдена.", againBody["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Delete_detach_keeps_child_without_parent()
    {
        TaskRecord parent = await _host.SeedTaskAsync("родитель detach", "http-detach");

        using var created = await _host.Client.PostAsync(
            "/api/tasks",
            JsonContent.Create(new { text = "ребёнок detach", parentId = parent.Id }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonObject childJson = Assert.IsType<JsonObject>(
            JsonNode.Parse(await created.Content.ReadAsStringAsync()));
        string childId = childJson["id"]!.GetValue<string>();

        using var response = await _host.Client.DeleteAsync(
            $"/api/tasks/{parent.Id}?mode=detach");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonObject result = Assert.IsType<JsonObject>(
            JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        Assert.Equal("detach", result["mode"]!.GetValue<string>());
        Assert.Equal(1, result["removedSubtasks"]!.GetValue<int>());

        JsonObject storedChild = await ReadStoredTask(Guid.Parse(childId));
        Assert.Null(storedChild["parentId"]);
        Assert.Equal(parent.TreeId.ToString(), storedChild["treeId"]!.GetValue<string>());

        using var unknownMode = await _host.Client.DeleteAsync(
            $"/api/tasks/{childId}?mode=unknown&confirm=true");
        Assert.Equal(HttpStatusCode.BadRequest, unknownMode.StatusCode);
        JsonObject unknownBody = Assert.IsType<JsonObject>(
            JsonNode.Parse(await unknownMode.Content.ReadAsStringAsync()));
        Assert.Equal("Неизвестный режим удаления 'unknown'.", unknownBody["error"]!.GetValue<string>());
    }


    [Fact]
    public async Task Telemetry_without_token_returns_401()
    {
        using var anon = new HttpClient { BaseAddress = _host.BaseAddress };
        using var response = await anon.GetAsync("/api/telemetry");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Telemetry_with_token_returns_200_and_json_structure()
    {
        using var response = await _host.Client.GetAsync("/api/telemetry?hours=24");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonObject json = Assert.IsType<JsonObject>(JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        Assert.Equal(24, json["windowHours"]!.GetValue<int>());
        Assert.NotNull(json["totals"]);
        Assert.NotNull(json["byEndpoint"]);
        Assert.NotNull(json["bySource"]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("169")]
    [InlineData("abc")]
    public async Task Telemetry_invalid_hours_returns_400(string hours)
    {
        using var response = await _host.Client.GetAsync($"/api/telemetry?hours={hours}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonObject json = Assert.IsType<JsonObject>(JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        Assert.Equal("hours: целое число от 1 до 168.", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Classify_records_request_action_with_duration()
    {
        using var post = await _host.Client.PostAsync(
            "/api/classify",
            JsonContent.Create(new { text = "купить молоко" }));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using var actionsResp = await _host.Client.GetAsync("/api/actions?limit=10");
        Assert.Equal(HttpStatusCode.OK, actionsResp.StatusCode);
        JsonArray actions = Assert.IsType<JsonArray>(JsonNode.Parse(await actionsResp.Content.ReadAsStringAsync()));
        var requestAction = actions.FirstOrDefault(a => a?["kind"]?.GetValue<string>() == "request" && a?["command"]?.GetValue<string>() == "classify");
        Assert.NotNull(requestAction);
        Assert.True(requestAction["durationMs"]!.GetValue<long>() >= 0);
    }

    [Fact]
    public async Task Voice_empty_body_records_rejected_action_with_status_400()
    {
        using var emptyContent = new ByteArrayContent(Array.Empty<byte>());
        emptyContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        using var voiceResp = await _host.Client.PostAsync("/api/voice?source=station", emptyContent);
        Assert.Equal(HttpStatusCode.BadRequest, voiceResp.StatusCode);

        using var actionsResp = await _host.Client.GetAsync("/api/actions?limit=10");
        Assert.Equal(HttpStatusCode.OK, actionsResp.StatusCode);
        JsonArray actions = Assert.IsType<JsonArray>(JsonNode.Parse(await actionsResp.Content.ReadAsStringAsync()));
        var requestAction = actions.FirstOrDefault(a => a?["kind"]?.GetValue<string>() == "request" && a?["command"]?.GetValue<string>() == "voice");
        Assert.NotNull(requestAction);
        Assert.Equal("rejected", requestAction["outcome"]!.GetValue<string>());
        Assert.Equal("Пустое тело запроса: ожидался WAV.", requestAction["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Speech_without_token_returns_401()
    {
        using var client = new HttpClient { BaseAddress = _host.BaseAddress };
        using var response = await client.PostAsJsonAsync("/api/speech", new SpeechRequest("Привет"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Speech_with_empty_text_returns_400()
    {
        using var response = await _host.Client.PostAsJsonAsync("/api/speech", new SpeechRequest(""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonObject json = Assert.IsType<JsonObject>(JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        Assert.Equal("Поле 'text' обязательно.", json["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task Speech_with_empty_voice_returns_400()
    {
        using var response = await _host.Client.PostAsJsonAsync("/api/speech", new SpeechRequest("Привет", "   "));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonObject json = Assert.IsType<JsonObject>(JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        Assert.Equal("Поле 'voice' пустое: нечего выбирать.", json["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task Speech_with_invalid_voice_name_returns_400()
    {
        using var response = await _host.Client.PostAsJsonAsync("/api/speech", new SpeechRequest("Привет", "../etc/passwd"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonObject json = Assert.IsType<JsonObject>(JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        Assert.Equal("Недопустимое имя голоса '../etc/passwd'. Разрешены буквы, цифры, точка, дефис и подчёркивание.", json["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task Speech_when_synthesizer_unavailable_returns_503_even_with_voice()
    {
        using var response = await _host.Client.PostAsJsonAsync("/api/speech", new SpeechRequest("Привет", "ru_RU-dmitri-medium"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        JsonObject json = Assert.IsType<JsonObject>(JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        Assert.Contains("sherpa-onnx", json["error"]?.GetValue<string>());
    }

    private async Task<JsonObject> ReadStoredTask(Guid id)
    {
        using var response = await _host.Client.GetAsync($"/api/tasks/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<JsonObject>(JsonNode.Parse(await response.Content.ReadAsStringAsync()));
    }

    /// <summary>Содержимое WAV для проверки «байты совпадают»: формат не важен, байты важны.</summary>
    private static byte[] WavBytes()
        => Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
}
