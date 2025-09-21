using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using ScanForge.Models;
using System;
using System.Threading.Tasks;

namespace ScanForge.Services;

/// <summary>
/// Serviço de notificação SignalR para conclusão de processamento
/// </summary>
public class SignalRNotifierService : IAsyncDisposable {
    private readonly HubConnection _connection;
    private readonly ILogger<SignalRNotifierService> _logger;
    private readonly string _hubUrl;
    private bool _isConnected = false;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    /// <summary>
    /// Inicializa conexão SignalR com VideoNest Hub
    /// </summary>
    /// <param name="configuration">Configuração com SignalR:HubUrl</param>
    /// <param name="logger">Logger estruturado</param>
    public SignalRNotifierService(IConfiguration configuration, ILogger<SignalRNotifierService> logger) {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hubUrl = configuration["SignalR:HubUrl"] ?? "http://videonest_service:8080/videoHub";

        _connection = new HubConnectionBuilder()
            .WithUrl(_hubUrl)
            .WithAutomaticReconnect()
            .Build();

        // Handlers de eventos de conexão
        _connection.Closed += async (error) => {
            _isConnected = false;
            _logger.LogWarning("SignalR conexão fechada: {Error}", error?.Message);
            await Task.CompletedTask;
        };

        _connection.Reconnected += async (connectionId) => {
            _isConnected = true;
            _logger.LogInformation("SignalR reconectado: {ConnectionId}", connectionId);
            await Task.CompletedTask;
        };

        _logger.LogInformation("SignalRNotifier inicializado para {HubUrl}", _hubUrl);
    }

    /// <summary>
    /// Notifica VideoNest sobre conclusão de processamento
    /// </summary>
    /// <param name="video">VideoResult processado</param>
    /// <remarks>
    /// Chama VideoNest Hub: VideoProcessed(videoId, status)
    /// Lazy connection com retry automático
    /// Graceful degradation se desconectado
    /// </remarks>
    public async Task NotifyCompletionAsync(VideoResult video) {
        ArgumentNullException.ThrowIfNull(video);

        try {
            // Lazy connection se necessário
            if (!_isConnected || _connection.State != HubConnectionState.Connected) {
                await _connectionLock.WaitAsync();
                try {
                    if (!_isConnected) {
                        await ConnectWithRetryAsync();
                    }
                } finally {
                    _connectionLock.Release();
                }
            }

            // Enviar notificação se conectado
            if (_connection.State == HubConnectionState.Connected) {
                await _connection.InvokeAsync("VideoProcessed", video.VideoId, video.Status);
                _logger.LogInformation("✅ Notificação SignalR enviada: VideoId={VideoId}, Status={Status}",
                    video.VideoId, video.Status);
            } else {
                _logger.LogWarning("⚠️ SignalR desconectado - notificação pulada: VideoId={VideoId}", video.VideoId);
            }
        } catch (Exception ex) {
            _logger.LogError(ex, "❌ Erro SignalR para VideoId={VideoId}", video.VideoId);
        }
    }

    /// <summary>
    /// Conecta com retry exponencial
    /// </summary>
    private async Task ConnectWithRetryAsync(int maxRetries = 3) {
        for (int retry = 1; retry <= maxRetries; retry++) {
            try {
                _logger.LogInformation("🔄 SignalR tentativa {Retry}/{MaxRetries} para {HubUrl}",
                    retry, maxRetries, _hubUrl);

                await _connection.StartAsync();
                _isConnected = true;
                _logger.LogInformation("✅ SignalR conectado: {HubUrl}", _hubUrl);
                return;
            } catch (Exception ex) when (retry < maxRetries) {
                _logger.LogWarning(ex, "⚠️ SignalR tentativa {Retry} falhou, retry em 3s", retry);
                await Task.Delay(3000);
            } catch (Exception ex) {
                _logger.LogError(ex, "❌ SignalR falhou após {MaxRetries} tentativas", maxRetries);
                _isConnected = false;
                return;
            }
        }
    }

    /// <summary>
    /// Libera recursos SignalR
    /// </summary>
    public async ValueTask DisposeAsync() {
        if (_connection != null) {
            try {
                await _connection.DisposeAsync();
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Erro ao dispose SignalR");
            }
        }
        _connectionLock?.Dispose();
    }
}