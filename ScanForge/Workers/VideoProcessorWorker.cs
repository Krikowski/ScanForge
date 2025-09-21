using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using ScanForge.DTOs;
using ScanForge.Repositories;
using ScanForge.Services;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScanForge.Workers;

/// <summary>
/// Worker Service para consumir mensagens RabbitMQ e processar vídeos assincronamente
/// Implementa RF2: fila de processamento com DLQ (bônus)
/// ✅ CORREÇÃO: Totalmente idempotente - CRIA exchanges se não existirem
/// </summary>
public class VideoProcessorWorker : BackgroundService, IDisposable {
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VideoProcessorWorker> _logger;
    private IConnection _connection;
    private IModel _channel;
    private readonly string _queueName;
    private readonly int _maxRetries;
    private bool _disposed = false;
    private readonly string _dlxName;
    private readonly string _dlqName;

    public VideoProcessorWorker(
        IServiceProvider serviceProvider,
        ILogger<VideoProcessorWorker> logger,
        IConfiguration configuration) {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var factory = new ConnectionFactory {
            HostName = configuration["RabbitMQ:HostName"] ?? "rabbitmq_hackathon",
            Port = int.Parse(configuration["RabbitMQ:Port"] ?? "5672"),
            UserName = configuration["RabbitMQ:UserName"] ?? "admin",
            Password = configuration["RabbitMQ:Password"] ?? "admin",
            AutomaticRecoveryEnabled = true,
            RequestedHeartbeat = TimeSpan.FromSeconds(60),
            NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
        };

        try {
            _logger.LogInformation("🐰 Iniciando conexão RabbitMQ...");
            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();

            _queueName = configuration["RabbitMQ:QueueName"] ?? "video_queue";
            _dlxName = configuration["RabbitMQ:DeadLetterExchange"] ?? "dlx_video_exchange";
            _dlqName = configuration["RabbitMQ:DeadLetterQueue"] ?? "dlq_video_queue";
            _maxRetries = int.Parse(configuration["RabbitMQ:RetryAttempts"] ?? "3");

            _logger.LogInformation("🔧 Configurando RabbitMQ idempotentemente...");
            SetupInfrastructure();
            _logger.LogInformation("✅ RabbitMQ configurado com sucesso!");
        } catch (Exception ex) {
            _logger.LogCritical(ex, "💥 FALHA CRÍTICA na inicialização RabbitMQ");
            CleanupConnection();
            throw; // Re-throw para container falhar graciosamente
        }
    }

    /// <summary>
    /// Configuração TOTALMENTE IDEMPOTENTE e ROBUSTA
    /// ✅ CRIA exchanges/queues/bindings se não existirem
    /// ✅ Trata 404 (NOT_FOUND) graciosamente
    /// </summary>
    private void SetupInfrastructure() {
        _logger.LogDebug("📋 Configurando DLX: {DlxName}", _dlxName);

        // ✅ DLX (Dead Letter Exchange)
        try {
            // Tenta acessar exchange existente
            _channel.ExchangeDeclarePassive(_dlxName);
            _logger.LogInformation("✅ DLX '{DlxName}' já existe", _dlxName);
        } catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404) {
            // Exchange não existe - CRIAR
            _channel.ExchangeDeclare(_dlxName, "direct", durable: true, autoDelete: false);
            _logger.LogInformation("✅ DLX '{DlxName}' CRIADO como 'direct'", _dlxName);
        } catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 406) {
            // Conflito de tipo - usar existente
            _logger.LogInformation("⚠️ DLX '{DlxName}' tem tipo diferente - usando existente", _dlxName);
        }

        _logger.LogDebug("📋 Configurando DLQ: {DlqName}", _dlqName);

        // ✅ DLQ (Dead Letter Queue)
        try {
            _channel.QueueDeclarePassive(_dlqName);
            _logger.LogInformation("✅ DLQ '{DlqName}' já existe", _dlqName);
        } catch (OperationInterruptedException) {
            _channel.QueueDeclare(_dlqName, durable: true, exclusive: false, autoDelete: false, arguments: null);
            _logger.LogInformation("✅ DLQ '{DlqName}' CRIADA", _dlqName);
        }

        // ✅ Binding DLQ → DLX
        try {
            _channel.QueueBind(_dlqName, _dlxName, "");
            _logger.LogInformation("✅ Binding DLQ→DLX: {DlqName} → {DlxName}", _dlqName, _dlxName);
        } catch (OperationInterruptedException) {
            _channel.QueueBind(_dlqName, _dlxName, "");
            _logger.LogInformation("✅ Binding criado: {DlqName} → {DlxName}", _dlqName, _dlxName);
        }

        _logger.LogDebug("📋 Configurando Queue principal: {QueueName}", _queueName);

        // ✅ Queue Principal (video_queue)
        try {
            _channel.QueueDeclarePassive(_queueName);
            _logger.LogInformation("✅ Queue '{QueueName}' já existe", _queueName);
        } catch (OperationInterruptedException) {
            var args = new Dictionary<string, object>
            {
                // Dead Letter Configuration
                { "x-dead-letter-exchange", _dlxName },
                { "x-dead-letter-routing-key", _dlqName },
                // TTL e limites
                { "x-message-ttl", int.Parse("300000") }, // 5min
                { "x-max-length", 10000 }, // Max 10k mensagens
                { "x-overflow", "drop-head" } // Drop oldest se cheia
            };

            _channel.QueueDeclare(_queueName, durable: true, exclusive: false, autoDelete: false, arguments: args);
            _logger.LogInformation("✅ Queue '{QueueName}' CRIADA com DLQ", _queueName);
        }

        _logger.LogInformation("🎉 Infra RabbitMQ completa: {QueueName} ↔ {DlqName} ↔ {DlxName}", _queueName, _dlqName, _dlxName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        stoppingToken.ThrowIfCancellationRequested();

        _logger.LogInformation("🚀 Iniciando consumer para '{QueueName}'...", _queueName);

        var consumer = new EventingBasicConsumer(_channel);

        consumer.Received += async (model, ea) => {
            var body = ea.Body.ToArray();
            var messageJson = Encoding.UTF8.GetString(body);

            _logger.LogInformation("📥 Mensagem recebida: {MessagePreview}",
                messageJson.Length > 100 ? messageJson.Substring(0, 100) + "..." : messageJson);

            VideoMessage? videoMessage = null;
            int videoIdForLogging = 0;

            try {
                // ✅ Scoped services
                await using var scope = _serviceProvider.CreateAsyncScope();
                var services = scope.ServiceProvider;

                var processingService = services.GetRequiredService<IVideoProcessingService>();
                var notifier = services.GetRequiredService<SignalRNotifierService>();
                var repo = services.GetRequiredService<IVideoRepository>();

                // Deserializar (RF2)
                videoMessage = JsonSerializer.Deserialize<VideoMessage>(
                    messageJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (videoMessage == null) {
                    _logger.LogWarning("❌ Mensagem JSON inválida - NACK imediato");
                    _channel.BasicNack(ea.DeliveryTag, false, false);
                    return;
                }

                videoIdForLogging = videoMessage.VideoId;
                _logger.LogInformation("🎬 Iniciando processamento: VideoId={VideoId}, File: {FileName}",
                    videoMessage.VideoId, Path.GetFileName(videoMessage.FilePath));

                // Processar vídeo (RF3-5)
                await processingService.ProcessVideoAsync(videoMessage);

                // Notificar (bônus SignalR)
                try {
                    var video = await repo.GetVideoByIdAsync(videoMessage.VideoId);
                    if (video != null) {
                        await notifier.NotifyCompletionAsync(video);
                        _logger.LogInformation("🔔 SignalR: VideoId={VideoId} notificado ({QrCount} QRs)",
                            videoMessage.VideoId, video.QRCodes?.Count ?? 0);
                    }
                } catch (Exception notifyEx) {
                    _logger.LogWarning(notifyEx, "⚠️ SignalR falhou para VideoId={VideoId} - processamento OK", videoMessage.VideoId);
                }

                // ✅ ACK - Mensagem processada com sucesso
                _channel.BasicAck(ea.DeliveryTag, false);
                _logger.LogInformation("✅ VideoId={VideoId} processado e ACK", videoMessage.VideoId);
            } catch (Exception ex) {
                var logVideoId = videoMessage?.VideoId ?? videoIdForLogging;
                _logger.LogError(ex, "💥 Erro processamento VideoId={VideoId}: {Message}", logVideoId, ex.Message);

                // Retry logic robusto
                var deliveryCount = GetDeliveryCount(ea);

                if (deliveryCount >= _maxRetries) {
                    _logger.LogError("🚫 VideoId={VideoId} FALHOU {MaxRetries} vezes → DLQ", logVideoId, _maxRetries);
                    _channel.BasicNack(ea.DeliveryTag, false, false); // Dead Letter Queue
                } else {
                    var nextAttempt = deliveryCount + 1;
                    _logger.LogWarning("🔄 VideoId={VideoId} tentativa {Attempt}/{MaxRetries} - requeue",
                        logVideoId, nextAttempt, _maxRetries);
                    _channel.BasicNack(ea.DeliveryTag, false, true); // Requeue
                }
            }
        };

        try {
            _channel.BasicConsume(_queueName, autoAck: false, consumer: consumer);
            _logger.LogInformation("🐰 Consumer ativo para '{QueueName}' (max {MaxRetries} retries)", _queueName, _maxRetries);
        } catch (Exception ex) {
            _logger.LogCritical(ex, "💥 FALHA ao iniciar consumer para '{QueueName}'", _queueName);
            throw;
        }

        // Manter worker vivo
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    /// <summary>
    /// Calcula número de tentativas a partir do header x-death
    /// </summary>
    private static int GetDeliveryCount(BasicDeliverEventArgs ea) {
        if (ea.BasicProperties.Headers?.ContainsKey("x-death") != true)
            return 0;

        try {
            var deathHeader = ea.BasicProperties.Headers["x-death"];
            if (deathHeader is System.Collections.Generic.IList<object> deathList) {
                return deathList.Count;
            }
            return 0;
        } catch (Exception) {
            return 0; // Fallback seguro
        }
    }

    /// <summary>
    /// Cleanup gracioso da conexão
    /// </summary>
    private void CleanupConnection() {
        try {
            _channel?.Close(200, "Cleanup");
            _connection?.Close(200, "Cleanup");
            _logger?.LogDebug("🧹 Conexão RabbitMQ limpa");
        } catch (Exception ex) {
            _logger?.LogWarning(ex, "⚠️ Erro no cleanup RabbitMQ");
        }
    }

    public void Dispose() {
        CleanupConnection();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}