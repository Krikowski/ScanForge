using ScanForge.DTOs;
using System.Threading.Tasks;

namespace ScanForge.Services;

public interface IVideoProcessingService {
    /// <summary>
    /// Processa vídeo para detecção de QR Codes
    /// </summary>
    /// <param name="message">Mensagem RabbitMQ com VideoId e FilePath</param>
    Task ProcessVideoAsync(VideoMessage message);
}