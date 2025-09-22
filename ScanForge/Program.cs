using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;
using ScanForge.Repositories;
using ScanForge.Services;
using ScanForge.Workers;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using StackExchange.Redis;
using MongoDB.Driver;
using System;
using System.Text.Json;

namespace ScanForge {
    public class Program {
        public static async Task Main(string[] args) {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .MinimumLevel.Override("ScanForge", LogEventLevel.Debug)
                .WriteTo.Console(
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{@Exception}")
                .WriteTo.File(
                    path: "logs/scanforge-.txt",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    formatter: new RenderedCompactJsonFormatter())
                .CreateLogger();

            try {
                var builder = Host.CreateApplicationBuilder(args);

                builder.Logging.ClearProviders();
                builder.Logging.AddSerilog();

                ConfigureServices(builder.Services, builder.Configuration);

                var host = builder.Build();

                var logger = host.Services.GetRequiredService<ILogger<Program>>();
                logger.LogInformation("🚀 ScanForge Worker Service iniciando em {Environment}...",
                    builder.Environment.EnvironmentName);

                // ✅ GARANTIR ÍNDICES UMA VEZ NO STARTUP
                using (var scope = host.Services.CreateScope()) {
                    var repo = scope.ServiceProvider.GetRequiredService<IVideoRepository>();
                    await repo.EnsureIndexesAsync();
                }

                // ✅ VALIDAR CONFIGURAÇÕES (BIND DIRETO)
                using (var scope = host.Services.CreateScope()) {
                    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                    var rabbitSection = config.GetSection("RabbitMQ");
                    var videoSection = config.GetSection("VideoProcessing");
                    var storageSection = config.GetSection("VideoStorage");

                    logger.LogInformation("⚙️ Configurações carregadas - " +
                        "RabbitMQ: {Host}:{Port}, Queue: {Queue}, " +
                        "FPS: {DefaultFps}/{OptimizedFps}, Temp: {TempPath}",
                        rabbitSection["HostName"], rabbitSection["Port"], rabbitSection["QueueName"],
                        videoSection["DefaultFps"], videoSection["OptimizedFps"],
                        storageSection["TempFramesPath"]);
                }

                await host.RunAsync();
            } catch (Exception ex) {
                Log.Fatal(ex, "💥 Erro fatal durante inicialização do ScanForge");
            } finally {
                await Log.CloseAndFlushAsync();
            }
        }

        private static void ConfigureServices(IServiceCollection services, IConfiguration configuration) {
            // ✅ WORKER SERVICE PRINCIPAL
            services.AddHostedService<VideoProcessorWorker>();

            // ✅ MONGODB
            services.AddSingleton<IMongoClient>(sp =>
                new MongoClient(configuration["MongoDB:ConnectionString"] ??
                    "mongodb://admin:admin@mongodb_hackathon:27017"));

            services.AddSingleton<IMongoDatabase>(sp =>
                sp.GetRequiredService<IMongoClient>()
                    .GetDatabase(configuration["MongoDB:DatabaseName"] ?? "Hackathon_FIAP"));

            // ✅ REDIS
            services.AddStackExchangeRedisCache(options => {
                options.Configuration = configuration["Redis:ConnectionString"] ?? "redis_hackathon:6379";
                options.InstanceName = "ScanForge:";
            });

            // ✅ REPOSITORIES
            services.AddScoped<IVideoRepository, VideoRepository>();

            // ✅ SERVICES
            services.AddScoped<IVideoProcessingService, VideoProcessingService>();
            services.AddScoped<SignalRNotifierService>();

            // ✅ REGISTRO CORRIGIDO: Interface para RabbitMQ se necessário
            // services.AddSingleton<IRabbitMQPublisher, RabbitMQPublisher>(); // Se usado no ScanForge

            // ✅ HEALTH CHECKS
            services.AddHealthChecks()
                .AddCheck("worker", () => HealthCheckResult.Healthy("Worker is running"))
                .AddCheck("self", () => HealthCheckResult.Healthy());

            // ✅ PROMETHEUS METRICS (porta 8081)
            services.AddMetricServer(options => options.Port = 8081);
        }
    }

    // ✅ CLASSES DE CONFIGURAÇÃO SIMPLES (BIND DIRETO)
    public class RabbitMQOptions {
        public string HostName { get; set; } = "localhost";
        public int Port { get; set; } = 5672;
        public string UserName { get; set; } = "guest";
        public string Password { get; set; } = "guest";
        public string QueueName { get; set; } = "video_queue";
        public string DeadLetterExchange { get; set; } = "dlx_video_exchange";
        public string DeadLetterQueue { get; set; } = "dlq_video_queue";
        public string VirtualHost { get; set; } = "/";
        public int MessageTtlMilliseconds { get; set; } = 300000;
        public int RetryAttempts { get; set; } = 3;
        public int RetryDelaySeconds { get; set; } = 5;
        public int HeartbeatSeconds { get; set; } = 60;
    }

    public class VideoProcessingOptions {
        public double DurationThreshold { get; set; } = 120;
        public double OptimizedFps { get; set; } = 0.5;
        public double DefaultFps { get; set; } = 1.0;
        public int FrameQualityCrf { get; set; } = 23;
        public string FFmpegPath { get; set; } = "/usr/bin/ffmpeg";
    }

    public class VideoStorageOptions {
        public string BasePath { get; set; } = "/uploads";
        public string TempFramesPath { get; set; } = "/tmp/scanforge_frames";
    }
}