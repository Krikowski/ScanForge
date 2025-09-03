using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using RabbitMQ.Client;
using ScanForge.Data;
using ScanForge.Models;
using System.Text;
using System.Threading.Tasks;

namespace ScanForge.Web.Controllers {
    [ApiController]
    [Route("api/[controller]")]
    public class QRCodeController : ControllerBase {
        private readonly VideoDbContext _dbContext;
        private readonly ILogger<QRCodeController> _logger;
        private readonly IConfiguration _configuration;

        public QRCodeController(VideoDbContext dbContext, ILogger<QRCodeController> logger, IConfiguration configuration) {
            _dbContext = dbContext;
            _logger = logger;
            _configuration = configuration;
        }

        [HttpPost]
        public async Task<IActionResult> CreateVideo([FromBody] CreateVideoRequest request) {
            if (string.IsNullOrEmpty(request.FilePath)) {
                return BadRequest(new { Message = "O caminho do arquivo é obrigatório." });
            }

            var video = new VideoDB {
                Title = request.Title ?? "Vídeo sem título",
                Description = request.Description ?? "",
                Duration = 0, // Ajustar conforme necessário
                FilePath = request.FilePath,
                Status = "Na Fila"
            };
            _dbContext.Videos.Add(video);
            await _dbContext.SaveChangesAsync();

            var factory = new ConnectionFactory {
                HostName = _configuration.GetSection("RabbitMQ:HostName").Value,
                UserName = _configuration.GetSection("RabbitMQ:UserName").Value,
                Password = _configuration.GetSection("RabbitMQ:Password").Value,
                Port = int.Parse(_configuration.GetSection("RabbitMQ:Port").Value)
            };
            using var connection = factory.CreateConnection();
            using var channel = connection.CreateModel();
            channel.QueueDeclare(queue: _configuration.GetSection("RabbitMQ:QueueName").Value, durable: true, exclusive: false, autoDelete: false, arguments: null);

            var message = JsonConvert.SerializeObject(new VideoMessage { VideoId = video.Id, FilePath = video.FilePath });
            var body = Encoding.UTF8.GetBytes(message);
            channel.BasicPublish(exchange: "", routingKey: _configuration.GetSection("RabbitMQ:QueueName").Value, basicProperties: null, body: body);
            _logger.LogInformation("Mensagem publicada na fila para Vídeo ID {Id}", video.Id);

            return Ok(new { Message = "Vídeo enviado para processamento.", VideoId = video.Id });
        }
    }

    public class CreateVideoRequest {
        public string Title { get; set; }
        public string Description { get; set; }
        public string FilePath { get; set; }
    }
}