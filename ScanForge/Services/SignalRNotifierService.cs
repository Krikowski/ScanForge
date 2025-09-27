using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using ScanForge.Models;
using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ScanForge.Services;

public interface ISignalRNotifierService {
    Task NotifyCompletionAsync(VideoResult video);
    Task NotifyProcessingStartedAsync(int videoId, string title);
    Task NotifyProcessingErrorAsync(int videoId, string error);
}

public class SignalRNotifierOptions {
    public string HubUrl { get; set; } = "http://videonest_service:8080/videoHub";
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;
}

public class SignalRNotifierService : ISignalRNotifierService, IDisposable {
    private IHubConnectionWrapper? _hubConnection;
    private readonly ILogger<SignalRNotifierService> _logger;
    private readonly string _hubUrl;
    private readonly int _maxRetries;
    private readonly int _retryDelayMs;
    private HttpClient _httpClient;
    private bool _disposed = false;

    private const int DEFAULT_MAX_RETRIES = 3;
    private const int DEFAULT_RETRY_DELAY_MS = 1000;

    public SignalRNotifierService(
        IConfiguration configuration,
        ILogger<SignalRNotifierService> logger) {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hubUrl = configuration["SignalR:HubUrl"] ?? "http://videonest_service:8080/videoHub";
        _maxRetries = configuration.GetValue<int>("SignalR:MaxRetries", DEFAULT_MAX_RETRIES);
        _retryDelayMs = configuration.GetValue<int>("SignalR:RetryDelayMs", DEFAULT_RETRY_DELAY_MS);

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        _ = InitializeConnectionAsync(); // fire and forget
    }

    private async Task InitializeConnectionAsync() {
        try {
            _logger.LogInformation("🔌 Inicializando SignalR para {Url}", _hubUrl);

            var builder = new HubConnectionBuilder()
                .WithUrl(_hubUrl)
                .WithAutomaticReconnect(new[]
                {
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(10)
                })
                .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

            var connection = builder.Build();
            _hubConnection = new HubConnectionWrapper(connection);

            connection.Closed += async (error) => {
                _logger.LogWarning("❌ SignalR conexão fechada: {Error}", error?.Message ?? "null");
                await Task.CompletedTask;
            };

            connection.Reconnecting += (error) => {
                _logger.LogWarning("🔄 SignalR reconectando: {Error}", error?.Message ?? "null");
                return Task.CompletedTask;
            };

            connection.Reconnected += async (connectionId) => {
                _logger.LogInformation("✅ SignalR reconectado: {ConnectionId}", connectionId);
                await Task.CompletedTask;
            };

            await connection.StartAsync();
            _logger.LogInformation("✅ SignalR conectado: {Url}", _hubUrl);
        } catch (Exception ex) {
            _logger.LogError(ex, "💥 Falha ao inicializar SignalR para {Url}", _hubUrl);
            _hubConnection = null;
        }
    }

    public async Task NotifyCompletionAsync(VideoResult video) {
        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected) {
            _logger.LogWarning("❌ SignalR não conectado (State: {State}) - pulando notificação para VideoId={VideoId}",
                _hubConnection?.State.ToString() ?? "null", video.VideoId);
            return;
        }

        for (int attempt = 1; attempt <= _maxRetries; attempt++) {
            try {
                _logger.LogDebug("🔄 Tentativa {Attempt}/{MaxRetries} para VideoId={VideoId}",
                    attempt, _maxRetries, video.VideoId);

                await _hubConnection.InvokeAsync("VideoProcessed", video);
                _logger.LogInformation("🔔 SignalR: VideoId={VideoId} notificado ({QRsCount} QRs)",
                    video.VideoId, video.QRCodes?.Count ?? 0);
                return;
            } catch (HubException ex) when (attempt < _maxRetries) {
                _logger.LogWarning(ex, "⚠️ SignalR falhou na tentativa {Attempt}/{MaxRetries} para VideoId={VideoId}: {Message}",
                    attempt, _maxRetries, video.VideoId, ex.Message);
                await Task.Delay(_retryDelayMs * attempt);
            } catch (Exception ex) {
                _logger.LogError(ex, "💥 Erro inesperado na tentativa {Attempt} para VideoId={VideoId}",
                    attempt, video.VideoId);
                if (attempt == _maxRetries) {
                    await FallbackHttpNotification(video);
                }
                break;
            }
        }

        if (_maxRetries > 0) {
            _logger.LogWarning("⚠️ Todas as {MaxRetries} tentativas falharam para VideoId={VideoId}",
                _maxRetries, video.VideoId);
        }
    }

    public async Task NotifyProcessingStartedAsync(int videoId, string title) {
        if (_hubConnection?.State != HubConnectionState.Connected) {
            _logger.LogDebug("SignalR indisponível - pulando notificação de início para VideoId={VideoId}", videoId);
            return;
        }

        try {
            await _hubConnection.InvokeAsync("VideoProcessingStarted", new { VideoId = videoId, Title = title });
            _logger.LogDebug("🔔 Início do processamento notificado para VideoId={VideoId}", videoId);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "⚠️ Falha ao notificar início do processamento para VideoId={VideoId}", videoId);
        }
    }

    public async Task NotifyProcessingErrorAsync(int videoId, string error) {
        if (_hubConnection?.State != HubConnectionState.Connected) {
            _logger.LogDebug("SignalR indisponível - pulando notificação de erro para VideoId={VideoId}", videoId);
            return;
        }

        try {
            await _hubConnection.InvokeAsync("VideoProcessingError", new { VideoId = videoId, Error = error });
            _logger.LogWarning("🔔 Erro de processamento notificado para VideoId={VideoId}", videoId);
        } catch (Exception ex) {
            _logger.LogError(ex, "💥 Falha ao notificar erro para VideoId={VideoId}", videoId);
        }
    }

    private async Task FallbackHttpNotification(VideoResult video) {
        try {
            var videoNestUrl = _hubUrl.Replace("/videoHub", "/api/videos");
            var updateUrl = $"{videoNestUrl}/{video.VideoId}/status";

            var payload = new {
                Status = video.Status,
                Duration = video.Duration,
                QRCodes = video.QRCodes?.Select(qr => new { qr.Content, qr.Timestamp }),
                ErrorMessage = video.ErrorMessage,
                LastUpdated = video.LastUpdated
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PutAsync(updateUrl, content);

            if (response.IsSuccessStatusCode) {
                _logger.LogInformation("✅ Fallback HTTP: VideoId={VideoId} atualizado via API", video.VideoId);
            } else {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("⚠️ Fallback HTTP falhou: {StatusCode} para VideoId={VideoId}. Resp: {Response}",
                    response.StatusCode, video.VideoId, errorContent);
            }
        } catch (Exception ex) {
            _logger.LogError(ex, "💥 Fallback HTTP falhou completamente para VideoId={VideoId}", video.VideoId);
        }
    }

    public async Task ReconnectAsync() {
        if (_hubConnection != null) {
            try {
                await _hubConnection.DisposeAsync();
            } catch (Exception ex) {
                _logger.LogWarning(ex, "⚠️ Erro ao descartar conexão antiga");
            }
        }

        _hubConnection = null;
        await InitializeConnectionAsync();
    }

    public void Dispose() {
        if (!_disposed) {
            try {
                _hubConnection?.DisposeAsync().GetAwaiter().GetResult();
                _httpClient?.Dispose();
            } catch (Exception ex) {
                _logger?.LogWarning(ex, "⚠️ Erro durante dispose do SignalRNotifier");
            } finally {
                _disposed = true;
            }
        }
    }
}
