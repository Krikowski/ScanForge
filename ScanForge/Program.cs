using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScanForge.Data;
using ScanForge.Services;

namespace ScanForge {
    public class Program {
        public static async Task Main(string[] args) {
            var builder = Host.CreateDefaultBuilder(args);

            // Configurar serviços
            builder.ConfigureServices((hostContext, services) => {
                // Registrar o Worker Service para consumir mensagens do RabbitMQ
                services.AddHostedService<RabbitMqConsumerService>();

                // Configurar conexão com o banco de dados PostgreSQL
                services.AddDbContext<VideoDbContext>(options =>
                    options.UseNpgsql(hostContext.Configuration.GetConnectionString("DefaultConnection")));

                // Configurar logging para console com nível mínimo de Information
                services.AddLogging(logging => {
                    logging.AddConsole();
                    logging.SetMinimumLevel(LogLevel.Information);
                });

                // Registrar IConfiguration para o Worker Service
                services.AddSingleton(hostContext.Configuration);
            });

            // Criar e executar o host
            var host = builder.Build();
            await host.RunAsync();
        }
    }
}