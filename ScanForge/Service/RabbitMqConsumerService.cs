using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using ScanForge.Data;
using ScanForge.Models;
using Xabe.FFmpeg;
using Microsoft.EntityFrameworkCore;
using ZXing;
using ZXing.Windows.Compatibility;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading;

namespace ScanForge.Services {
    public class RabbitMqConsumerService : BackgroundService {
        private readonly ILogger<RabbitMqConsumerService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private IConnection _connection;
        private IModel _channel;
        private readonly string _queueName;
        private readonly string _videoBasePath;
        private readonly string _ffmpegPath;
        private readonly ConnectionFactory _factory;

        public RabbitMqConsumerService(ILogger<RabbitMqConsumerService> logger, IServiceScopeFactory scopeFactory, IConfiguration configuration) {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _queueName = configuration["RabbitMQ:QueueName"] ?? "video_queue";
            _videoBasePath = configuration["VideoStorage:BasePath"] ?? "C:\\videos";
            _ffmpegPath = configuration["FFmpeg:Path"] ?? "C:\\ffmpeg\\bin";

            _factory = new ConnectionFactory {
                HostName = configuration["RabbitMQ:HostName"] ?? "host.docker.internal",
                UserName = configuration["RabbitMQ:UserName"] ?? "admin",
                Password = configuration["RabbitMQ:Password"] ?? "admin",
                Port = int.Parse(configuration["RabbitMQ:Port"] ?? "5672"),
                RequestedHeartbeat = TimeSpan.FromSeconds(60),
                AutomaticRecoveryEnabled = true
            };

            ConnectWithRetry();
        }

        private void ConnectWithRetry() {
            int retryCount = 0;
            int maxRetries = 5;
            int delayMs = 5000;

            while (retryCount < maxRetries) {
                try {
                    _logger.LogInformation("Tentando conectar ao RabbitMQ em {HostName}:{Port} com usuário {UserName}", _factory.HostName, _factory.Port, _factory.UserName);
                    _connection = _factory.CreateConnection();
                    _channel = _connection.CreateModel();
                    _channel.QueueDeclare(queue: _queueName, durable: true, exclusive: false, autoDelete: false, arguments: null);
                    _logger.LogInformation("Conexão com RabbitMQ estabelecida com sucesso para a fila {QueueName}", _queueName);
                    return;
                } catch (Exception ex) {
                    retryCount++;
                    _logger.LogError(ex, "Falha ao conectar ao RabbitMQ (tentativa {RetryCount}/{MaxRetries})", retryCount, maxRetries);
                    if (retryCount >= maxRetries) {
                        throw new Exception("Não foi possível conectar ao RabbitMQ após várias tentativas", ex);
                    }
                    Thread.Sleep(delayMs);
                }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            FFmpeg.SetExecutablesPath(_ffmpegPath);
            _logger.LogInformation("FFmpeg configurado em {FFmpegPath}", _ffmpegPath);

            var consumer = new EventingBasicConsumer(_channel);
            consumer.Received += async (model, ea) => {
                try {
                    var body = ea.Body.ToArray();
                    var message = Encoding.UTF8.GetString(body);
                    var videoMessage = JsonConvert.DeserializeObject<VideoMessage>(message);

                    // Usar o FilePath diretamente, garantindo compatibilidade com Windows
                    var mappedFilePath = videoMessage.FilePath;
                    if (mappedFilePath.StartsWith("/app/videos")) {
                        var fileName = Path.GetFileName(videoMessage.FilePath);
                        mappedFilePath = Path.Combine(_videoBasePath, fileName).Replace("/", "\\").Trim();
                    } else {
                        mappedFilePath = videoMessage.FilePath.Replace("/", "\\").Trim().Replace("\r", "").Replace("\n", "");
                    }

                    _logger.LogInformation("FilePath recebido: {OriginalPath}, FilePath mapeado: {MappedPath}, Exists: {Exists}", videoMessage.FilePath, mappedFilePath, File.Exists(mappedFilePath));

                    if (!File.Exists(mappedFilePath)) {
                        _logger.LogWarning("Arquivo de vídeo não encontrado: {MappedFilePath}", mappedFilePath);

                        using (var scope = _scopeFactory.CreateScope()) {
                            var dbContext = scope.ServiceProvider.GetRequiredService<VideoDbContext>();
                            var video = await dbContext.Videos.FindAsync(videoMessage.VideoId);
                            if (video != null) {
                                video.Status = "Erro";
                                await dbContext.SaveChangesAsync(stoppingToken);
                            }
                        }
                        
                        _channel.BasicNack(deliveryTag: ea.DeliveryTag, multiple: false, requeue: false);
                        return;
                    }

                    IMediaInfo mediaInfo = await FFmpeg.GetMediaInfo(mappedFilePath);

                    using (var scope = _scopeFactory.CreateScope()) {
                        var dbContext = scope.ServiceProvider.GetRequiredService<VideoDbContext>();
                        var video = await dbContext.Videos
                            .Include(v => v.QRCodes)
                            .FirstOrDefaultAsync(v => v.Id == videoMessage.VideoId, stoppingToken);

                        if (video == null) {
                            _logger.LogWarning("Vídeo ID {VideoId} não encontrado no banco", videoMessage.VideoId);
                            _channel.BasicNack(deliveryTag: ea.DeliveryTag, multiple: false, requeue: false);
                            return;
                        }

                        video.Status = "Processando";
                        video.Duration = (int)mediaInfo.Duration.TotalSeconds;
                        await dbContext.SaveChangesAsync(stoppingToken);
                        _logger.LogInformation("Vídeo ID {VideoId} atualizado para status 'Processando' com duração {Duration}s", videoMessage.VideoId, video.Duration);
                    }

                    var tempFramesDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempFramesDir);
                    var frameOutputPattern = Path.Combine(tempFramesDir, "frame-%03d.png").Replace("/", "\\");

                    try {
                        _logger.LogInformation("Iniciando extração de frames para {FilePath} com padrão {FrameOutputPattern}", mappedFilePath, frameOutputPattern);

                        var conversion = FFmpeg.Conversions.New()
                            .AddParameter($"-i \"{mappedFilePath}\" -vf fps=1 \"{frameOutputPattern}\"");
                        _logger.LogInformation("Comando FFmpeg gerado: {Command}", conversion.Build());
                        await conversion.Start(stoppingToken);

                        _logger.LogInformation("Frames extraídos para {TempFramesDir}", tempFramesDir);

                        var frameFiles = Directory.GetFiles(tempFramesDir, "*.png")
                            .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f).Split('-')[1]))
                            .ToArray();

                        var qrResults = new ConcurrentBag<QRCodeResult>();

                        Parallel.ForEach(frameFiles, frameFile => {
                            try {
                                int frameNum = int.Parse(Path.GetFileNameWithoutExtension(frameFile).Split('-')[1]);
                                int timestamp = frameNum - 1;

                                using (var bitmap = (Bitmap)Image.FromFile(frameFile)) {
                                    var reader = new BarcodeReaderGeneric();
                                    var source = new BitmapLuminanceSource(bitmap);
                                    var result = reader.Decode(source);

                                    if (result != null) {
                                        qrResults.Add(new QRCodeResult { Content = result.Text, Timestamp = timestamp });
                                        _logger.LogInformation("QR Code encontrado no frame {FrameFile}: {QrCodeText} no timestamp {Timestamp}s", frameFile, result.Text, timestamp);
                                    }
                                }
                            } catch (Exception ex) {
                                _logger.LogWarning(ex, "Erro ao escanear QR Code no frame {FrameFile}", frameFile);
                            }
                        });

                        using (var scope = _scopeFactory.CreateScope()) {
                            var dbContext = scope.ServiceProvider.GetRequiredService<VideoDbContext>();
                            var video = await dbContext.Videos
                                .Include(v => v.QRCodes)
                                .FirstOrDefaultAsync(v => v.Id == videoMessage.VideoId, stoppingToken);

                            if (video != null) {
                                foreach (var qr in qrResults) {
                                    qr.VideoId = video.Id;
                                    video.QRCodes.Add(qr);
                                }
                                video.Status = "Concluído";
                                await dbContext.SaveChangesAsync(stoppingToken);
                                _logger.LogInformation("Vídeo ID {VideoId} atualizado para status 'Concluído' com {QrCount} QR Codes salvos", videoMessage.VideoId, qrResults.Count);
                            }
                        }
                    } catch (Exception ex) {
                        _logger.LogError(ex, "Erro ao processar vídeo {FilePath}", mappedFilePath);
                        using (var scope = _scopeFactory.CreateScope()) {
                            var dbContext = scope.ServiceProvider.GetRequiredService<VideoDbContext>();
                            var video = await dbContext.Videos.FindAsync(videoMessage.VideoId);
                            if (video != null) {
                                video.Status = "Erro";
                                await dbContext.SaveChangesAsync(stoppingToken);
                            }
                        }
                        _channel.BasicNack(deliveryTag: ea.DeliveryTag, multiple: false, requeue: true);
                        return;
                    } finally {
                        if (Directory.Exists(tempFramesDir)) {
                            Directory.Delete(tempFramesDir, true);
                        }
                    }

                    _channel.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
                } catch (Exception ex) {
                    _logger.LogError(ex, "Erro ao processar mensagem na fila {QueueName}", _queueName);
                    _channel.BasicNack(deliveryTag: ea.DeliveryTag, multiple: false, requeue: true);
                }
            };

            _channel.BasicConsume(queue: _queueName, autoAck: false, consumer: consumer);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        public override void Dispose() {
            _channel?.Close();
            _connection?.Close();
            _channel?.Dispose();
            _connection?.Dispose();
            base.Dispose();
        }
    }
}