using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ScanForge.Data;
using ScanForge.Models;
using Xabe.FFmpeg;

namespace ScanForge.Services {
    public class RabbitMqConsumerService : BackgroundService {
        private readonly ILogger _logger; private readonly IServiceScopeFactory _scopeFactory; private readonly IConnection _connection; private readonly IModel _channel; private readonly string _queueName;

        public RabbitMqConsumerService(ILogger<RabbitMqConsumerService> logger, IServiceScopeFactory scopeFactory, IConfiguration configuration) {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _queueName = configuration.GetSection("RabbitMQ:QueueName").Value ?? "video_queue";

            try {
                var factory = new ConnectionFactory {
                    HostName = configuration.GetSection("RabbitMQ:HostName").Value ?? "rabbitmq",
                    UserName = configuration.GetSection("RabbitMQ:UserName").Value ?? "admin",
                    Password = configuration.GetSection("RabbitMQ:Password").Value ?? "admin",
                    Port = int.Parse(configuration.GetSection("RabbitMQ:Port").Value ?? "5672"),
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(30)
                };

                _logger.LogInformation("Tentando conectar ao RabbitMQ em {HostName}:{Port} para a fila {QueueName}", factory.HostName, factory.Port, _queueName);

                _connection = factory.CreateConnection();
                _channel = _connection.CreateModel();
                _logger.LogInformation("Conexão com RabbitMQ estabelecida com sucesso para a fila {QueueName}", _queueName);
            } catch (Exception ex) {
                _logger.LogError(ex, "Falha ao conectar ao RabbitMQ para a fila {QueueName}", _queueName);
                throw;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            _logger.LogInformation("RabbitMqConsumerService iniciado, aguardando mensagens na fila {QueueName}...", _queueName);

            var consumer = new EventingBasicConsumer(_channel);
            consumer.Received += async (model, ea) =>
            {
                try {
                    var body = ea.Body.ToArray();
                    var message = Encoding.UTF8.GetString(body);
                    _logger.LogInformation("Mensagem recebida na fila {QueueName}: {Message}", _queueName, message);

                    // Desserializar a mensagem
                    var videoMessage = JsonConvert.DeserializeObject<VideoMessage>(message);

                    // Determinar o FilePath com base no ambiente
                    string mappedFilePath;
                    if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                        // No Windows, usar o FilePath original
                        mappedFilePath = videoMessage.FilePath.Replace("/", "\\"); // Normaliza para \
                    } else {
                        // No Docker (Linux), mapear para /videos
                        var fileName = Path.GetFileName(videoMessage.FilePath.Replace("\\", "/"));
                        mappedFilePath = Path.Combine("/videos", fileName).Replace("\\", "/"); // Garante /videos/teste.mp4
                    }
                    _logger.LogInformation("FilePath original: {OriginalPath}, FilePath mapeado: {MappedPath}", videoMessage.FilePath, mappedFilePath);

                    // Listar arquivos no diretório para depuração
                    var videoDir = Path.GetDirectoryName(mappedFilePath);
                    var videoFiles = Directory.Exists(videoDir) ? Directory.GetFiles(videoDir) : Array.Empty<string>();
                    _logger.LogInformation("Arquivos no diretório {VideoDir}: {Files}", videoDir, string.Join(", ", videoFiles));

                    // Atualizar status para "Processando"
                    using (var scope = _scopeFactory.CreateScope()) {
                        var dbContext = scope.ServiceProvider.GetRequiredService<VideoDbContext>();
                        var video = await dbContext.Videos.FindAsync(videoMessage.VideoId, stoppingToken);
                        if (video == null) {
                            _logger.LogWarning("Vídeo ID {VideoId} não encontrado no banco", videoMessage.VideoId);
                            _channel.BasicNack(deliveryTag: ea.DeliveryTag, multiple: false, requeue: false);
                            return;
                        }

                        video.Status = "Processando";
                        await dbContext.SaveChangesAsync(stoppingToken);
                        _logger.LogInformation("Vídeo ID {VideoId} atualizado para status 'Processando'", videoMessage.VideoId);
                    }

                    // Verificar se o arquivo de vídeo existe
                    if (!File.Exists(mappedFilePath)) {
                        _logger.LogWarning("Arquivo de vídeo não encontrado: {MappedFilePath}", mappedFilePath);
                        using (var scope = _scopeFactory.CreateScope()) {
                            var dbContext = scope.ServiceProvider.GetRequiredService<VideoDbContext>();
                            var video = await dbContext.Videos.FindAsync(videoMessage.VideoId, stoppingToken);
                            if (video != null) {
                                video.Status = "Falha";
                                video.Description = $"Arquivo de vídeo não encontrado: {mappedFilePath}";
                                await dbContext.SaveChangesAsync(stoppingToken);
                                _logger.LogInformation("Vídeo ID {VideoId} atualizado para status 'Falha' devido a arquivo ausente", videoMessage.VideoId);
                            }
                        }
                        _channel.BasicNack(deliveryTag: ea.DeliveryTag, multiple: false, requeue: false);
                        return;
                    }

                    // Criar diretório temporário para frames
                    var tempFramesDir = Path.Combine(Path.GetTempPath(), $"frames_{videoMessage.VideoId}_{Guid.NewGuid()}");
                    Directory.CreateDirectory(tempFramesDir);

                    try {
                        // Configurar FFmpeg
                        FFmpeg.SetExecutablesPath(Environment.OSVersion.Platform == PlatformID.Win32NT ? @"C:\ffmpeg\bin" : "/usr/bin");
                        var frameOutputPattern = Path.Combine(tempFramesDir, "frame-%03d.png").Replace("\\", "/");

                        // Extrair 1 frame por segundo
                        var conversion = FFmpeg.Conversions.New()
                            .AddParameter($"-i \"{mappedFilePath}\" -vf fps=1 -c:v png \"{frameOutputPattern}\"");
                        await conversion.Start(stoppingToken);

                        _logger.LogInformation("Frames extraídos com sucesso para o vídeo ID {VideoId} em {TempFramesDir}", videoMessage.VideoId, tempFramesDir);

                        // Aqui você pode adicionar a lógica para escanear QR Codes nos frames (com ZXing.Net)
                        // Por enquanto, atualizamos o status para "Concluído" como placeholder
                        using (var scope = _scopeFactory.CreateScope()) {
                            var dbContext = scope.ServiceProvider.GetRequiredService<VideoDbContext>();
                            var video = await dbContext.Videos.FindAsync(videoMessage.VideoId, stoppingToken);
                            if (video != null) {
                                video.Status = "Concluído";
                                video.Description = "Frames extraídos com sucesso";
                                await dbContext.SaveChangesAsync(stoppingToken);
                                _logger.LogInformation("Vídeo ID {VideoId} atualizado para status 'Concluído'", videoMessage.VideoId);
                            }
                        }
                    } catch (Exception ex) {
                        _logger.LogError(ex, "Erro ao processar vídeo ID {VideoId}: {Message}", videoMessage.VideoId, ex.Message);
                        using (var scope = _scopeFactory.CreateScope()) {
                            var dbContext = scope.ServiceProvider.GetRequiredService<VideoDbContext>();
                            var video = await dbContext.Videos.FindAsync(videoMessage.VideoId, stoppingToken);
                            if (video != null) {
                                video.Status = "Falha";
                                video.Description = $"Erro ao processar vídeo: {ex.Message}";
                                await dbContext.SaveChangesAsync(stoppingToken);
                                _logger.LogInformation("Vídeo ID {VideoId} atualizado para status 'Falha'", videoMessage.VideoId);
                            }
                        }
                        _channel.BasicNack(deliveryTag: ea.DeliveryTag, multiple: false, requeue: true);
                        return;
                    } finally {
                        // Limpar frames temporários
                        if (Directory.Exists(tempFramesDir)) {
                            Directory.Delete(tempFramesDir, true);
                            _logger.LogInformation("Frames temporários removidos: {TempFramesDir}", tempFramesDir);
                        }
                    }

                    // Confirmar recebimento da mensagem
                    _channel.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
                } catch (Exception ex) {
                    _logger.LogError(ex, "Erro ao processar mensagem na fila {QueueName}", _queueName);
                    _channel.BasicNack(deliveryTag: ea.DeliveryTag, multiple: false, requeue: true);
                }
            };

            _channel.BasicConsume(queue: _queueName, autoAck: false, consumer: consumer);

            while (!stoppingToken.IsCancellationRequested) {
                await Task.Delay(1000, stoppingToken);
            }
        }

        public override void Dispose() {
            try {
                _channel?.Close();
                _connection?.Close();
                _logger.LogInformation("Conexão com RabbitMQ fechada com sucesso");
            } catch (Exception ex) {
                _logger.LogError(ex, "Erro ao fechar conexão com RabbitMQ");
            }
            base.Dispose();
        }
    }

}