using ScanForge.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ScanForge.Repositories;

public interface IVideoRepository {
    /// <summary>
    /// Busca vídeo por ID no MongoDB
    /// </summary>
    /// <param name="id">ID único do vídeo</param>
    /// <returns>Vídeo encontrado ou null</returns>
    Task<VideoResult?> GetVideoByIdAsync(int id);

    /// <summary>
    /// Atualiza completamente um vídeo no MongoDB
    /// </summary>
    /// <param name="video">Objeto VideoResult atualizado</param>
    Task UpdateVideoAsync(VideoResult video);

    /// <summary>
    /// Salva novo vídeo no MongoDB (para testes E2E)
    /// </summary>
    /// <param name="video">Novo objeto VideoResult</param>
    Task SaveVideoAsync(VideoResult video);

    /// <summary>
    /// Atualiza status e metadados do vídeo
    /// </summary>
    /// <param name="videoId">ID do vídeo</param>
    /// <param name="status">Novo status ("Na Fila", "Processando", "Concluído", "Erro")</param>
    /// <param name="errorMessage">Mensagem de erro (opcional)</param>
    /// <param name="duration">Duração em segundos (opcional)</param>
    Task UpdateStatusAsync(int videoId, string status, string? errorMessage = null, int duration = 0);

    /// <summary>
    /// Adiciona QR Codes detectados ao vídeo
    /// </summary>
    /// <param name="videoId">ID do vídeo</param>
    /// <param name="qrs">Lista de QRCodeResult com conteúdo e timestamp</param>
    Task AddQRCodesAsync(int videoId, List<QRCodeResult> qrs);

    /// <summary>
    /// NOVO: Garante que os índices otimizados existem no MongoDB
    /// Método idempotente, deve ser chamado uma vez no startup
    /// </summary>
    Task EnsureIndexesAsync();
}