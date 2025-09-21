using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;

namespace ScanForge.Models;

/// <summary>
/// Resultado do processamento de vídeo no MongoDB
/// </summary>
public class VideoResult {
    /// <summary>
    /// ID único do vídeo (chave primária)
    /// </summary>
    [BsonId]
    [BsonRepresentation(BsonType.Int32)]
    public int VideoId { get; set; }

    /// <summary>
    /// Título do vídeo
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Descrição opcional
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Caminho do arquivo de vídeo
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Status do processamento
    /// </summary>
    public string Status { get; set; } = "Na Fila";

    /// <summary>
    /// Data de criação (UTC)
    /// </summary>
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Duração do vídeo em segundos
    /// </summary>
    public int Duration { get; set; }

    /// <summary>
    /// Mensagem de erro (se aplicável)
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Lista de QR Codes detectados
    /// </summary>
    public List<QRCodeResult> QRCodes { get; set; } = new List<QRCodeResult>();

    /// <summary>
    /// Última atualização do registro
    /// Necessário para compatibilidade com VideoNest API
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Resultado individual de QR Code
/// </summary>
public class QRCodeResult {
    /// <summary>
    /// Conteúdo do QR Code
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    /// Timestamp em segundos
    /// </summary>
    public int Timestamp { get; set; }
}