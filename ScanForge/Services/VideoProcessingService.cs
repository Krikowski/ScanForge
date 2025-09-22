using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScanForge.DTOs;
using ScanForge.Models;
using ScanForge.Repositories;
using SkiaSharp;
using ZXing;
using ZXing.SkiaSharp;
using ZXing.Common;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ScanForge.Services;

public class VideoProcessingService : IVideoProcessingService {
    private readonly IVideoRepository _videoRepository;
    private readonly ILogger<VideoProcessingService> _logger;
    private readonly VideoProcessingOptions _options;
    private readonly string _tempFramesPath;

    public VideoProcessingService(
        IVideoRepository videoRepository,
        ILogger<VideoProcessingService> logger,
        IOptions<VideoProcessingOptions> options,
        IOptions<VideoStorageOptions> storageOptions) {
        _videoRepository = videoRepository ?? throw new ArgumentNullException(nameof(videoRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _tempFramesPath = storageOptions?.Value?.TempFramesPath ?? "/tmp/scanforge_frames";

        // ✅ CRIAÇÃO AUTOMÁTICA DO DIRETÓRIO TEMPORÁRIO
        try {
            Directory.CreateDirectory(_tempFramesPath);
            _logger.LogDebug("📁 Diretório temporário criado: {TempFramesPath}", _tempFramesPath);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "⚠️ Falha ao criar diretório temporário: {TempFramesPath}", _tempFramesPath);
        }
    }

    /// <summary>
    /// Processa vídeo extraíndo frames e detectando QR Codes
    /// </summary>
    public async Task ProcessVideoAsync(VideoMessage message) {
        ArgumentNullException.ThrowIfNull(message);

        var videoId = message.VideoId;
        var filePath = message.FilePath;

        _logger.LogInformation("🎬 === INICIANDO PROCESSAMENTO VideoId={VideoId}: {FileName} ===", videoId, Path.GetFileName(filePath));

        try {
            // ✅ VALIDAÇÃO DE ARQUIVO
            if (!File.Exists(filePath)) {
                var errorMsg = $"Arquivo não encontrado: {filePath}";
                _logger.LogError(errorMsg);
                await _videoRepository.UpdateStatusAsync(videoId, "Erro", errorMsg);
                return;
            }

            _logger.LogInformation("✅ Arquivo resolvido em: {FilePath}", filePath);

            // 1. Análise de metadados com FFProbe (usando Process)
            var videoInfo = await AnalyzeVideoMetadataAsync(filePath);
            if (videoInfo == null) {
                var errorMsg = "Falha na análise de metadados do vídeo";
                _logger.LogError(errorMsg);
                await _videoRepository.UpdateStatusAsync(videoId, "Erro", errorMsg);
                return;
            }

            _logger.LogInformation("📊 Análise FFmpeg: Duração {Duration}s, {Width}x{Height}, {Codec}",
                videoInfo.Duration, videoInfo.Width, videoInfo.Height, videoInfo.Codec);

            // 2. Atualiza status para "Processando" com duração
            await _videoRepository.UpdateStatusAsync(videoId, "Processando", duration: videoInfo.Duration);
            _logger.LogInformation("🔄 Status atualizado para 'Processando' - Duração: {Duration}s", videoInfo.Duration);

            // 3. Configuração de FPS baseada na duração (otimização)
            var fps = videoInfo.Duration > _options.DurationThreshold
                ? _options.OptimizedFps
                : _options.DefaultFps;
            _logger.LogInformation("⚙️ Configuração: FPS={Fps} (threshold: {Threshold}s)", fps, _options.DurationThreshold);

            // 4. Extração de frames
            var frames = await ExtractFramesAsync(filePath, fps, videoInfo.Duration, videoId);
            if (!frames.Any()) {
                var errorMsg = "Nenhum frame foi extraído do vídeo";
                _logger.LogWarning(errorMsg);
                await _videoRepository.UpdateStatusAsync(videoId, "Concluído", errorMsg, videoInfo.Duration);
                return;
            }

            _logger.LogInformation("🔍 ✅ {FrameCount} frames extraídos em {Elapsed}s",
                frames.Count, frames.FirstOrDefault()?.ExtractionTime ?? 0);

            // ✅ CORREÇÃO PRINCIPAL: Coleta thread-safe de QR Codes
            _logger.LogInformation("🔍 Processando {FrameCount} frames em paralelo para QR detection", frames.Count);

            var qrResults = new ConcurrentBag<QRCodeResult>(); // ✅ THREAD-SAFE COLLECTION

            // ✅ PROCESSAMENTO PARALELO COM OPÇÕES DE CONCORRÊNCIA CONTROLADA
            var parallelOptions = new ParallelOptions {
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4) // ✅ LIMITA CONCORRÊNCIA
            };

            Parallel.ForEach(frames, parallelOptions, frame => {
                try {
                    var detectedContent = DetectQRInFrame(frame.Path);
                    if (!string.IsNullOrEmpty(detectedContent)) {
                        // ✅ CADA DETECÇÃO É ADICIONADA DE FORMA SEGURA
                        var qrResult = new QRCodeResult {
                            Content = detectedContent,
                            Timestamp = frame.Timestamp
                        };

                        qrResults.Add(qrResult);

                        _logger.LogInformation("✅ 🎯 QR Code detectado! Frame {Timestamp}s (t={Timestamp}s): {Content}",
                            frame.Timestamp, frame.Timestamp, detectedContent);
                    }
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "⚠️ Erro ao processar frame {FramePath} (t={Timestamp}s)",
                        frame.Path, frame.Timestamp);
                    // ✅ NÃO PARA O PROCESSAMENTO - CONTINUA COM OUTROS FRAMES
                }
            });

            // ✅ ORDENAÇÃO POR TIMESTAMP PARA CONSISTÊNCIA NO BANCO
            var sortedQrResults = qrResults
                .OrderBy(qr => qr.Timestamp)
                .ToList();

            _logger.LogInformation("✅ Processamento de frames concluído: {Detected}/{Total} frames com QR OK",
                sortedQrResults.Count, frames.Count);

            // 5. Atualização final no MongoDB
            if (sortedQrResults.Any()) {
                await _videoRepository.AddQRCodesAsync(videoId, sortedQrResults);
                _logger.LogInformation("💾 ✅ {Count} QR Codes salvos no MongoDB para VideoId={VideoId}",
                    sortedQrResults.Count, videoId);
            } else {
                _logger.LogInformation("ℹ️ Nenhum QR Code detectado em {FrameCount} frames", frames.Count);
            }

            // 6. Status final "Concluído"
            await _videoRepository.UpdateStatusAsync(videoId, "Concluído", duration: videoInfo.Duration);
            _logger.LogInformation("🎉 === PROCESSAMENTO CONCLUÍDO VideoId={VideoId}: {Detected} QR(s) detectado(s) ===",
                videoId, sortedQrResults.Count);

            // ✅ LIMPEZA DE FRAMES TEMPORÁRIOS
            CleanupFrames(frames.Select(f => f.Path).ToList());
            _logger.LogDebug("🧹 Frames temporários removidos");

        } catch (Exception ex) {
            _logger.LogError(ex, "💥 Erro crítico no processamento VideoId={VideoId}: {Message}", videoId, ex.Message);
            await _videoRepository.UpdateStatusAsync(videoId, "Erro", ex.Message);
        }
    }

    /// <summary>
    /// Analisa metadados do vídeo usando FFProbe via Process
    /// </summary>
    private async Task<VideoInfo?> AnalyzeVideoMetadataAsync(string filePath) {
        try {
            _logger.LogDebug("🔍 Analisando metadados com FFProbe...");

            var startInfo = new ProcessStartInfo {
                FileName = _options.FFmpegPath,
                Arguments = $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            var output = new System.Text.StringBuilder();
            var error = new System.Text.StringBuilder();

            process.OutputDataReceived += (s, e) => {
                if (e.Data != null) output.AppendLine(e.Data);
            };

            process.ErrorDataReceived += (s, e) => {
                if (e.Data != null) error.AppendLine(e.Data);
            };

            // ✅ CORREÇÃO: Usar Start() ao invés de StartAsync()
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0) {
                _logger.LogWarning("FFProbe falhou com código {ExitCode}. Output: {Output}", process.ExitCode, output.ToString());
                return null;
            }

            var jsonOutput = output.ToString();
            _logger.LogDebug("FFProbe JSON: {Json}", jsonOutput);

            // ✅ PARSING MELHORADO DO JSON
            return ParseVideoInfo(jsonOutput);
        } catch (Exception ex) {
            _logger.LogError(ex, "Erro na análise de metadados: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Parseia informações do vídeo do JSON do FFProbe
    /// </summary>
    private VideoInfo? ParseVideoInfo(string jsonOutput) {
        try {
            // Regex para extrair valores do JSON
            var durationMatch = Regex.Match(jsonOutput, @"""duration"":\s*""?(\d+(?:\.\d+)?)""?");
            var widthMatch = Regex.Match(jsonOutput, @"""width"":\s*(\d+)");
            var heightMatch = Regex.Match(jsonOutput, @"""height"":\s*(\d+)");
            var codecMatch = Regex.Match(jsonOutput, @"""codec_name"":\s*""([^""]+)""");

            if (!durationMatch.Success) {
                _logger.LogWarning("Não foi possível extrair duração do JSON");
                return null;
            }

            return new VideoInfo {
                Duration = (int)Math.Round(double.Parse(durationMatch.Groups[1].Value)),
                Width = widthMatch.Success ? int.Parse(widthMatch.Groups[1].Value) : 0,
                Height = heightMatch.Success ? int.Parse(heightMatch.Groups[1].Value) : 0,
                Codec = codecMatch.Success ? codecMatch.Groups[1].Value : "unknown"
            };
        } catch (Exception ex) {
            _logger.LogError(ex, "Erro no parsing do JSON do FFProbe: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Extrai frames do vídeo usando FFmpeg via Process
    /// </summary>
    private async Task<List<VideoFrame>> ExtractFramesAsync(string filePath, double fps, int totalDuration, int videoId) {
        var frames = new List<VideoFrame>();
        var stopwatch = Stopwatch.StartNew();

        try {
            // Calcula intervalos de extração
            var frameInterval = 1.0 / fps;
            var totalFrames = Math.Min((int)(totalDuration * fps), 10); // ✅ Limite de 10 frames para teste

            _logger.LogDebug("📸 Extraindo {TotalFrames} frames a {Fps} FPS (intervalo: {Interval}s)",
                totalFrames, fps, frameInterval);

            // ✅ EXTRAÇÃO SEQUENCIAL DE FRAMES
            for (int i = 0; i < totalFrames; i++) {
                var timestamp = i * frameInterval;

                // ✅ CORREÇÃO: Usar videoId passado como parâmetro
                var framePath = Path.Combine(_tempFramesPath, $"frame_{videoId:D6}_{i:D4}.jpg");

                var arguments = $"-ss {timestamp:F2} -i \"{filePath}\" -vframes 1 -q:v {_options.FrameQualityCrf} \"{framePath}\" -y";

                var startInfo = new ProcessStartInfo {
                    FileName = _options.FFmpegPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = new Process { StartInfo = startInfo };

                // ✅ CORREÇÃO: Usar Start() + WaitForExitAsync()
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                if (process.ExitCode == 0 && File.Exists(framePath)) {
                    var fileInfo = new FileInfo(framePath);
                    if (fileInfo.Length > 0) // ✅ Verificar se o arquivo não está vazio
                    {
                        frames.Add(new VideoFrame {
                            Path = framePath,
                            Timestamp = (int)timestamp,
                            ExtractionTime = stopwatch.Elapsed.TotalSeconds
                        });
                        _logger.LogDebug("✅ Frame extraído: {FramePath} (t={Timestamp}s)", framePath, timestamp);
                    } else {
                        _logger.LogWarning("Frame vazio gerado: {FramePath}", framePath);
                        File.Delete(framePath);
                    }
                } else {
                    _logger.LogWarning("Falha ao extrair frame em t={Timestamp}s (ExitCode: {ExitCode})", timestamp, process.ExitCode);
                    if (File.Exists(framePath)) File.Delete(framePath);
                }
            }
        } catch (Exception ex) {
            _logger.LogError(ex, "Erro na extração de frames: {Message}", ex.Message);
        } finally {
            stopwatch.Stop();
        }

        return frames;
    }

    /// <summary>
    /// Detecta QR Code em um frame usando ZXing
    /// </summary>
    private string? DetectQRInFrame(string framePath) {
        try {
            using var bitmap = SKBitmap.Decode(framePath);
            if (bitmap == null) {
                _logger.LogDebug("Falha ao decodificar bitmap: {FramePath}", framePath);
                return null;
            }

            // ✅ ZXing.SkiaSharp aceita SKBitmap diretamente
            var reader = new BarcodeReader {
                AutoRotate = true,
                Options = new DecodingOptions {
                    TryHarder = true,
                    PossibleFormats = new[] { BarcodeFormat.QR_CODE }
                }
            };

            var result = reader.Decode(bitmap);
            if (result != null) {
                _logger.LogDebug("QR Code detectado: {Content}", result.Text);
            }

            return result?.Text;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Erro na detecção de QR no frame {FramePath}", framePath);
            return null;
        }
    }

    /// <summary>
    /// Limpa frames temporários
    /// </summary>
    private void CleanupFrames(IEnumerable<string> framePaths) {
        try {
            var count = 0;
            foreach (var framePath in framePaths) {
                if (File.Exists(framePath)) {
                    File.Delete(framePath);
                    count++;
                }
            }
            _logger.LogDebug("🧹 {Count} frames temporários removidos", count);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Erro na limpeza de frames temporários");
        }
    }
}

/// <summary>
/// Informações do vídeo extraídas do FFProbe
/// </summary>
public class VideoInfo {
    public int Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Codec { get; set; } = string.Empty;
}

/// <summary>
/// Representa um frame extraído
/// </summary>
public class VideoFrame {
    public string Path { get; set; } = string.Empty;
    public int Timestamp { get; set; }
    public double ExtractionTime { get; set; }
}

/// <summary>
/// Opções de processamento de vídeo
/// </summary>
public class VideoProcessingOptions {
    public double DurationThreshold { get; set; } = 120;
    public double OptimizedFps { get; set; } = 0.5;
    public double DefaultFps { get; set; } = 1.0;
    public int FrameQualityCrf { get; set; } = 23;
    public string FFmpegPath { get; set; } = "/usr/bin/ffmpeg";
}

/// <summary>
/// Opções de armazenamento
/// </summary>
public class VideoStorageOptions {
    public string BasePath { get; set; } = "/uploads";
    public string TempFramesPath { get; set; } = "/tmp/scanforge_frames";
}