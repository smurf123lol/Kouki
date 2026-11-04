using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Kouki.Kagedama;
using Kouki.Kagedama.Api;
using Kouki.Kagedama.Api.Api;
using Kouki.Kagedama.Api.Auth;
using Kouki.Kagedama.Api.Services;
using Kouki.Kagedama.Commands;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Hosting;
using Kouki.Mugura.Dispatcher;
using Kouki.Shimi.ServiceAbstractions;
using Kouki.Ubusuna.DependencyInjection;
using Kouki.Un;

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
/// Тесты эндпоинтов OTA прошивки (/api/firmware и /api/firmware/image) по спеке T16.
/// </summary>
public sealed class FirmwareEndpointTests
{
    [Fact]
    public void StationSemver_ParsesAndOrdersCorrectly()
    {
        Assert.True(StationSemver.TryParse("1.0.0", out var v100));
        Assert.True(StationSemver.TryParse("1.0.1", out var v101));
        Assert.True(StationSemver.TryParse("0.1.0", out var v010));
        Assert.True(StationSemver.TryParse("v1.2.3", out var v123));
        Assert.True(StationSemver.TryParse("2.0.0-rc1", out var v200rc1));
        Assert.True(StationSemver.TryParse("2.0.0", out var v200));

        Assert.True(v101 > v100);
        Assert.True(v100 > v010);
        Assert.True(v200 > v200rc1);
        Assert.Equal(v100, new StationSemver(1, 0, 0));

        Assert.False(StationSemver.TryParse("invalid", out _));
        Assert.False(StationSemver.TryParse("1.0", out _));
        Assert.False(StationSemver.TryParse("", out _));
    }

    [Fact]
    public async Task GetFirmware_WithoutToken_Returns401Unauthorized()
    {
        await using var server = await TestFirmwareServer.StartAsync();
        using var client = server.CreateClient(withAuth: false);

        var response = await client.GetAsync("/api/firmware");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("unauthorized", json?["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task GetFirmware_NotConfigured_Returns404FirmwareNotConfigured()
    {
        await using var server = await TestFirmwareServer.StartAsync(configureDir: false);
        using var client = server.CreateClient(withAuth: true);

        var response = await client.GetAsync("/api/firmware");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("firmware-not-configured", json?["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task GetFirmware_EmptyDirectory_Returns404FirmwareNotFound()
    {
        await using var server = await TestFirmwareServer.StartAsync(configureDir: true);
        using var client = server.CreateClient(withAuth: true);

        var response = await client.GetAsync("/api/firmware");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("firmware-not-found", json?["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task GetFirmware_CurrentIsOlder_Returns200WithUpdateTrueAndMatchingMetadata()
    {
        await using var server = await TestFirmwareServer.StartAsync(configureDir: true);
        byte[] fw101 = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        server.AddFirmware("1.0.1", fw101);
        byte[] fw100 = [0x11, 0x22];
        server.AddFirmware("1.0.0", fw100);

        string expectedSha = Convert.ToHexStringLower(SHA256.HashData(fw101));
        long expectedSize = fw101.Length;

        using var client = server.CreateClient(withAuth: true);

        // 1.0.0 старее 1.0.1 -> update: true
        var response = await client.GetAsync("/api/firmware?current=1.0.0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("1.0.1", json?["version"]?.GetValue<string>());
        Assert.Equal(expectedSha, json?["sha256"]?.GetValue<string>());
        Assert.Equal(expectedSize, json?["size"]?.GetValue<long>());
        Assert.Equal("/api/firmware/image?version=1.0.1", json?["url"]?.GetValue<string>());
        Assert.True(json?["update"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GetFirmware_CurrentIsEqualOrNewer_Returns200WithUpdateFalse()
    {
        await using var server = await TestFirmwareServer.StartAsync(configureDir: true);
        byte[] fw101 = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        server.AddFirmware("1.0.1", fw101);

        string expectedSha = Convert.ToHexStringLower(SHA256.HashData(fw101));
        long expectedSize = fw101.Length;

        using var client = server.CreateClient(withAuth: true);

        // current равен 1.0.1
        var responseSame = await client.GetAsync("/api/firmware?current=1.0.1");
        Assert.Equal(HttpStatusCode.OK, responseSame.StatusCode);

        var jsonSame = await responseSame.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("1.0.1", jsonSame?["version"]?.GetValue<string>());
        Assert.Equal(expectedSha, jsonSame?["sha256"]?.GetValue<string>());
        Assert.Equal(expectedSize, jsonSame?["size"]?.GetValue<long>());
        Assert.False(jsonSame?["update"]?.GetValue<bool>());

        // current новее (например 1.0.2)
        var responseNewer = await client.GetAsync("/api/firmware?current=1.0.2");
        Assert.Equal(HttpStatusCode.OK, responseNewer.StatusCode);
        var jsonNewer = await responseNewer.Content.ReadFromJsonAsync<JsonObject>();
        Assert.False(jsonNewer?["update"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GetFirmwareImage_ValidVersion_ReturnsBytesETagAndNoStore()
    {
        await using var server = await TestFirmwareServer.StartAsync(configureDir: true);
        byte[] fwBytes = [0xCA, 0xFE, 0xBA, 0xBE, 0x01, 0x02, 0x03];
        server.AddFirmware("0.9.5", fwBytes);
        string expectedSha = Convert.ToHexStringLower(SHA256.HashData(fwBytes));

        using var client = server.CreateClient(withAuth: true);

        var response = await client.GetAsync("/api/firmware/image?version=0.9.5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"\"{expectedSha}\"", response.Headers.ETag?.ToString());
        Assert.True(response.Headers.CacheControl?.NoStore);

        byte[] receivedBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(fwBytes, receivedBytes);

        // Запрос несуществующей версии -> 404
        var responseMissing = await client.GetAsync("/api/firmware/image?version=9.9.9");
        Assert.Equal(HttpStatusCode.NotFound, responseMissing.StatusCode);
    }

    [Fact]
    public async Task GetFirmwareImage_PathTraversal_Returns404NotFoundNot500()
    {
        await using var server = await TestFirmwareServer.StartAsync(configureDir: true);
        using var client = server.CreateClient(withAuth: true);

        string[] badVersions = ["../secret", "..\\secret", "/etc/passwd", "..", "foo/bar"];
        foreach (string bad in badVersions)
        {
            var response = await client.GetAsync($"/api/firmware/image?version={Uri.EscapeDataString(bad)}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal("firmware-not-found", json?["error"]?.GetValue<string>());
        }
    }

    [Fact]
    public async Task GetFirmware_FailClosed_EmptyTokenRejects()
    {
        await using var server = await TestFirmwareServer.StartAsync(configureDir: true, token: string.Empty);
        using var client = server.CreateClient(withAuth: false);

        var response = await client.GetAsync("/api/firmware");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class TestFirmwareServer : IAsyncDisposable
    {
        public const string DefaultToken = "test-token-123";
        private readonly WebApplication _app;
        private readonly string _tempDir;
        public string FirmwareDir { get; }

        private TestFirmwareServer(WebApplication app, string tempDir, string firmwareDir, Uri baseAddress)
        {
            _app = app;
            _tempDir = tempDir;
            FirmwareDir = firmwareDir;
            BaseAddress = baseAddress;
        }

        public Uri BaseAddress { get; }

        public static async Task<TestFirmwareServer> StartAsync(
            bool configureDir = true,
            string? token = DefaultToken)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "kouki-fw-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string fwDir = Path.Combine(tempDir, "firmware");
            if (configureDir)
            {
                Directory.CreateDirectory(fwDir);
            }

            string dbPath = Path.Combine(tempDir, "test.sqlite");

            var serverOptions = new KoukiServerOptions
            {
                DatabasePath = dbPath,
                Token = token ?? string.Empty,
                FirmwareDirectory = configureDir ? fwDir : string.Empty,
            };

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            builder.Services.AddSingleton(serverOptions);
            builder.Services.AddSingleton<IOptions<KoukiServerOptions>>(Options.Create(serverOptions));
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<ICommandConfirmation, ApiCommandConfirmation>();

            builder.Services.AddKoukiCore(new KoukiHostSettings
            {
                DatabasePath = dbPath,
                DefaultSource = TaskSources.Api,
            });
            builder.Services.AddUbusuna();
            builder.Services.AddUn();
            builder.Services.AddSingleton<ISpeechTranscriber, TestTranscriber>();
            builder.Services.AddSingleton<VoiceIntakeService>(sp => new VoiceIntakeService(
                sp.GetRequiredService<ISpeechTranscriber>(),
                sp.GetRequiredService<TaskIntakeService>(),
                sp.GetRequiredService<ILogger<VoiceIntakeService>>()));

            WebApplication app = builder.Build();
            KoukiHosting.InitializeKouki(app.Services, "test");
            app.UseMiddleware<TokenAuthMiddleware>();
            app.MapKoukiApi();

            await app.StartAsync();

            IServer server = app.Services.GetRequiredService<IServer>();
            IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel не сообщил адрес.");
            Uri baseAddress = new(addresses.Addresses.First());

            return new TestFirmwareServer(app, tempDir, fwDir, baseAddress);
        }

        public void AddFirmware(string version, byte[] bytes)
        {
            string path = Path.Combine(FirmwareDir, $"kouki-station-{version}.bin");
            File.WriteAllBytes(path, bytes);
        }

        private sealed class TestTranscriber : ISpeechTranscriber
        {
            public Task<TranscriptionResult> TranscribeAsync(Stream audio, AudioFormat format, CancellationToken cancellationToken = default)
                => Task.FromResult(TranscriptionResult.Empty);
        }

        public HttpClient CreateClient(bool withAuth)
        {
            var client = new HttpClient { BaseAddress = BaseAddress };
            if (withAuth)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DefaultToken);
            }
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }
}
