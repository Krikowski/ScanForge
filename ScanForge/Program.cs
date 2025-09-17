using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScanForge.Data;
using ScanForge.Services;
using MongoDB.Driver;

namespace ScanForge {
    public class Program {
        public static async Task Main(string[] args) {
            var builder = Host.CreateDefaultBuilder(args);

            builder.ConfigureServices((hostContext, services) => {
                services.AddHostedService<RabbitMqConsumerService>();
                services.AddDbContext<VideoDbContext>(options =>
                    options.UseNpgsql(hostContext.Configuration.GetConnectionString("DefaultConnection")));
                // Adicionado: MongoDB client singleton
                services.AddSingleton<IMongoClient>(sp =>
                    new MongoClient(hostContext.Configuration["MongoDB:ConnectionString"]));
                services.AddSingleton(sp =>
                    sp.GetRequiredService<IMongoClient>().GetDatabase(hostContext.Configuration["MongoDB:DatabaseName"]));
                services.AddLogging(logging => {
                    logging.AddConsole();
                    logging.SetMinimumLevel(LogLevel.Information);
                });
                services.AddSingleton(hostContext.Configuration);
                services.AddScoped<VideoDbContext>();
            });

            var host = builder.Build();
            await host.RunAsync();
        }
    }
}