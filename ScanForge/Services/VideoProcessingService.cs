using FFMpegCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ScanForge.DTOs;
using ScanForge.Models;
using ScanForge.Repositories;
using ScanForge.Services;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ZXing;
using ZXing.SkiaSharp;

namespace ScanForge.Services;

/// <summary>
/// Serviço principal para processamento de vídeos com FFMpegCore e ZXing
/// Implementa RF3-5: decodificação, extração de frames e detecção de QR Codes
/// </summary>
public class VideoProcessingService : IVideoProcessingService {
    private readonly IVideoRepository _repository;
    private readonly ILogger<VideoProcessingService> _logger;
    private readonly string _basePath;
    private readonly string _tempFramesPath;
    private readonly int _durationThreshold;
    private readonly double _optimizedFps;
    private readonly double _defaultFps;

    /// <summary>
    /// Construtor com injeção de dependências (3 parâmetros: Repository, Logger, Configuration)
    /// </summary>
    public VideoProcessingService(
        IVideoRepository repository,
        ILogger<VideoProcessingService> logger,
        IConfiguration configuration) {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Configurações do appsettings.json
        _basePath = configuration["VideoStorage:BasePath"] ?? "/app/uploads";
        _tempFramesPath = configuration["VideoStorage:TempFramesPath"] ?? "/tmp/scanforge_frames";
        _durationThreshold = int.Parse(configuration["Optimization:DurationThreshold"] ?? "120");
        _optimizedFps = double.Parse(configuration["Optimization:OptimizedFps"] ?? "0.5");
        _defaultFps = double.Parse(configuration["Optimization:DefaultFps"] ?? "1.0");
    }

    /// <summary>
    /// Processa vídeo completo: análise de duração, extração de frames, detecção de QR Codes
    /// Implementa processamento paralelo (bônus) e otimização de FPS baseada em duração
    /// </summary>
    public async Task ProcessVideoAsync(VideoMessage message) {

        var startTime = DateTime.UtcNow;
        _logger.LogInformation("🎬 === INICIANDO PROCESSAMENTO VideoId={VideoId}: {FileName} ===",
            message.VideoId, Path.GetFileName(message.FilePath));

        try {
            // ✅ Arquivo resolvido (já funcionando!)
            string resolvedPath = ResolveFilePath(message.FilePath);
            message.FilePath = resolvedPath;
            _logger.LogInformation("✅ Arquivo resolvido em: {ResolvedPath}", resolvedPath);

            // Atualiza status inicial
            await _repository.UpdateStatusAsync(message.VideoId, "Processando");

            // Análise FFmpeg (RF3)
            _logger.LogDebug("🔍 Analisando metadados com FFProbe...");
            var analysis = await FFProbe.AnalyseAsync(message.FilePath);
            var duration = (int)analysis.Duration.TotalSeconds;
            _logger.LogInformation("📊 Análise FFmpeg: Duração {Duration}s, {Width}x{Height}, {VideoCodec}",
                duration, analysis.PrimaryVideoStream.Width, analysis.PrimaryVideoStream.Height, analysis.PrimaryVideoStream.CodecName);

            await _repository.UpdateStatusAsync(message.VideoId, "Processando", duration: duration);

            // Configuração FPS
            var fps = duration > _durationThreshold ? _optimizedFps : _defaultFps;
            _logger.LogInformation("⚙️ Configuração: FPS={Fps} (threshold: {Threshold}s)", fps, _durationThreshold);

            // Extrair frames (RF3)
            _logger.LogInformation("🎬 Extraindo frames com FFMpeg...");
            Directory.CreateDirectory(_tempFramesPath);

            var outputPattern = Path.Combine(_tempFramesPath, "frame_%04d.png");
            await FFMpegArguments
                .FromFileInput(message.FilePath)
                .OutputToFile(outputPattern, overwrite: true, options => options
                    .WithFramerate(fps)
                    .WithVideoCodec("png")
                    .ForceFormat("image2"))
                .ProcessAsynchronously();

            var frameFiles = Directory.GetFiles(_tempFramesPath, "frame_*.png")
                .OrderBy(f => f)
                .ToList();

            _logger.LogInformation("🔍 ✅ {FrameCount} frames extraídos em {Elapsed}s",
                frameFiles.Count, DateTime.Now.Subtract(startTime).TotalSeconds);

            // Processar QR Codes (RF4) - com tratamento de erro por frame
            var qrResults = new List<QRCodeResult>();
            _logger.LogInformation("🔍 Processando {FrameCount} frames em paralelo para QR detection", frameFiles.Count);

            var processedFrames = 0;
            Parallel.ForEach(frameFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, frameFile =>
            {
                try {
                    // ✅ SkiaSharp corrigido no Dockerfile
                    using var bitmap = SKBitmap.Decode(frameFile);
                    if (bitmap == null) {
                        _logger.LogWarning("⚠️ Frame inválido: {FrameFile}", frameFile);
                        return;
                    }

                    var reader = new BarcodeReader();
                    var result = reader.Decode(bitmap);

                    if (result != null) {
                        var frameNumber = int.Parse(Path.GetFileNameWithoutExtension(frameFile).Split('_')[1]);
                        var timestamp = (int)(frameNumber / fps);

                        lock (qrResults) {
                            qrResults.Add(new QRCodeResult {
                                Content = result.Text,
                                Timestamp = timestamp
                            });
                        }

                        _logger.LogInformation("✅ 🎯 QR Code detectado! Frame {FrameNumber}s (t={Timestamp}s): {Content}",
                            frameNumber, timestamp, result.Text);
                    }

                    Interlocked.Increment(ref processedFrames);
                } catch (DllNotFoundException ex) when (ex.Message.Contains("libSkiaSharp")) {
                    _logger.LogError(ex, "❌ CRÍTICO: SkiaSharp não instalado - instale no Dockerfile!");
                    throw; // Re-throw para falha visível
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "⚠️ Erro ao processar frame {FrameFile}", frameFile);
                }
            });

            _logger.LogInformation("✅ Processamento de frames concluído: {Processed}/{Total} frames OK", processedFrames, frameFiles.Count);

            // Deduplicar QRs
            if (qrResults.Any()) {
                var uniqueQrs = qrResults
                    .GroupBy(q => q.Content)
                    .Select(g => g.OrderBy(x => x.Timestamp).First()) // Pega primeiro timestamp por conteúdo
                    .OrderBy(q => q.Timestamp)
                    .ToList();

                _logger.LogInformation("📈 {QrCount} QR Codes únicos detectados (de {Total} totais)",
                    uniqueQrs.Count, qrResults.Count);

                await _repository.AddQRCodesAsync(message.VideoId, uniqueQrs);
            } else {
                _logger.LogInformation("📭 Nenhum QR Code detectado no vídeo {VideoId}", message.VideoId);
            }

            // Finalizar (RF5-6)
            await _repository.UpdateStatusAsync(message.VideoId, "Concluído");

            // Cleanup
            try {
                Directory.Delete(_tempFramesPath, true);
                _logger.LogDebug("🧹 Frames temporários removidos");
            } catch (Exception cleanupEx) {
                _logger.LogWarning(cleanupEx, "⚠️ Erro ao remover frames temporários");
            }

            var totalTime = DateTime.UtcNow.Subtract(startTime).TotalSeconds;
            _logger.LogInformation("🎉 ✅ === PROCESSAMENTO CONCLUÍDO: VideoId={VideoId}, QRs={Count}, Tempo={TotalTime:F1}s ===",
                message.VideoId, qrResults.Count, totalTime);

        } catch (Exception ex) {
            _logger.LogError(ex, "💥 === ERRO CRÍTICO no processamento VideoId={VideoId}: {Message} ===",
                message.VideoId, ex.Message);

            await _repository.UpdateStatusAsync(message.VideoId, "Erro", ex.Message);

            throw new InvalidOperationException($"Falha processamento {message.VideoId}", ex);
        }
    }

    /// <summary>
    /// Resolve path do arquivo com fallback para volumes compartilhados
    /// Correção: Evita loop infinito em FileNotFound
    /// </summary>
    private string ResolveFilePath(string originalPath) {
        var possiblePaths = new[]
        {
        originalPath,
        Path.Combine("/uploads", Path.GetFileName(originalPath)),
        Path.Combine("/app/uploads", Path.GetFileName(originalPath))
    };

        foreach (var path in possiblePaths) {
            if (File.Exists(path)) {
                return path;
            }
        }

        throw new FileNotFoundException($"Arquivo não encontrado em nenhum path: {string.Join(", ", possiblePaths)}");
    }
}