using System.Text.Json.Serialization;

namespace ScanForge.DTOs;

/// <summary>
/// Mensagem RabbitMQ para processamento de vídeo
/// </summary>
public class VideoMessage {
    /// <summary>
    /// ID único do vídeo
    /// </summary>
    [JsonPropertyName("VideoId")]
    public int VideoId { get; set; }

    /// <summary>
    /// Caminho completo do arquivo de vídeo
    /// </summary>
    [JsonPropertyName("FilePath")]
    public string FilePath { get; set; } = string.Empty;
}