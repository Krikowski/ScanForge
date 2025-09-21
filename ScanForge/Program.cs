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

                // NOVA: Garantir índices uma vez no startup
                using (var scope = host.Services.CreateScope()) {
                    var repo = scope.ServiceProvider.GetRequiredService<IVideoRepository>();
                    await repo.EnsureIndexesAsync(); // Chama a criação de índices (idempotente)
                }

                await host.RunAsync();
            } catch (Exception ex) {
                Log.Fatal(ex, "💥 Erro fatal durante inicialização do ScanForge");
            } finally {
                await Log.CloseAndFlushAsync();
            }
        }

        private static void ConfigureServices(IServiceCollection services, IConfiguration configuration) {
            // Worker Service principal
            services.AddHostedService<VideoProcessorWorker>();

            // MongoDB
            services.AddSingleton<IMongoClient>(sp =>
                new MongoClient(configuration["MongoDB:ConnectionString"] ??
                    "mongodb://admin:admin@mongodb:27017"));

            services.AddSingleton<IMongoDatabase>(sp =>
                sp.GetRequiredService<IMongoClient>()
                    .GetDatabase(configuration["MongoDB:DatabaseName"] ?? "Hackathon_FIAP"));

            // Redis
            services.AddStackExchangeRedisCache(options => {
                options.Configuration = configuration["Redis:ConnectionString"] ?? "redis:6379";
                options.InstanceName = "ScanForge:";
            });

            // Repositórios
            services.AddScoped<IVideoRepository, VideoRepository>();

            // Serviços principais
            services.AddScoped<IVideoProcessingService, VideoProcessingService>();
            services.AddScoped<SignalRNotifierService>();

            // Health Checks
            services.AddHealthChecks()
                .AddCheck("worker", () => HealthCheckResult.Healthy("Worker is running"));

            // Prometheus Metrics (porta 8081)
            services.AddMetricServer(options => options.Port = 8081);
        }
    }
}