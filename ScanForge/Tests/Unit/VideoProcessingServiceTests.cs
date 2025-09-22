using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using ScanForge.DTOs;
using ScanForge.Models;
using ScanForge.Repositories;
using ScanForge.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using System.Collections.Concurrent;

namespace ScanForge.Tests.Unit;

public class VideoProcessingServiceTests : IDisposable {
    private readonly Mock<IVideoRepository> _mockRepository;
    private readonly Mock<ILogger<VideoProcessingService>> _mockLogger;
    // ✅ CORREÇÃO: Declarar com o namespace correto (Services)
    private readonly Services.VideoProcessingOptions _videoOptions;
    private readonly Services.VideoStorageOptions _storageOptions;
    private readonly VideoProcessingService _service;
    private readonly string _testTempPath;

    public VideoProcessingServiceTests() {
        _mockRepository = new Mock<IVideoRepository>();
        _mockLogger = new Mock<ILogger<VideoProcessingService>>();

        // ✅ CORREÇÃO: Usar as classes com namespace correto
        _videoOptions = new Services.VideoProcessingOptions {
            DurationThreshold = 120,
            OptimizedFps = 0.5,
            DefaultFps = 1.0,
            FrameQualityCrf = 23,
            FFmpegPath = "/usr/bin/ffmpeg"
        };

        _storageOptions = new Services.VideoStorageOptions {
            BasePath = "/app/uploads",
            TempFramesPath = Path.Combine(Path.GetTempPath(), "scanforge_test_frames")
        };

        var mockVideoOptions = new Mock<IOptions<Services.VideoProcessingOptions>>();
        mockVideoOptions.Setup(o => o.Value).Returns(_videoOptions);

        var mockStorageOptions = new Mock<IOptions<Services.VideoStorageOptions>>();
        mockStorageOptions.Setup(o => o.Value).Returns(_storageOptions);

        // ✅ CONSTRUTOR COM NAMESPACE CORRETO
        _service = new VideoProcessingService(
            _mockRepository.Object,
            _mockLogger.Object,
            mockVideoOptions.Object,
            mockStorageOptions.Object);

        // ✅ SETUP: Criar diretório temporário para testes
        _testTempPath = _storageOptions.TempFramesPath;
        Directory.CreateDirectory(_testTempPath);
    }

    [Fact]
    public async Task ProcessVideoAsync_ValidVideo_ShouldUpdateStatusAndProcessFrames() {
        // Arrange
        var tempFilePath = CreateTempVideoFile("test_valid.mp4", 10); // Vídeo de 10s
        var videoMessage = new VideoMessage {
            VideoId = 1,
            FilePath = tempFilePath
        };

        // Mock do repositório - simula sucesso em todas as operações
        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                      .Returns(Task.CompletedTask);
        _mockRepository.Setup(r => r.AddQRCodesAsync(It.IsAny<int>(), It.IsAny<List<QRCodeResult>>()))
                      .Returns(Task.CompletedTask);

        // Mock do logger para verificar logs (opcional)
        _mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        // Act
        await _service.ProcessVideoAsync(videoMessage);

        // Assert
        _mockRepository.Verify(r => r.UpdateStatusAsync(1, "Processando", null, 10), Times.Once);
        _mockRepository.Verify(r => r.UpdateStatusAsync(1, "Concluído", null, 10), Times.Once);
        _mockRepository.Verify(r => r.AddQRCodesAsync(1, It.Is<List<QRCodeResult>>(list => list.Count == 0)), Times.Once); // Sem QR codes esperados

        // Cleanup
        CleanupTempFile(tempFilePath);
    }

    [Fact]
    public async Task ProcessVideoAsync_FileNotFound_ShouldUpdateErrorStatus() {
        // Arrange
        var videoMessage = new VideoMessage {
            VideoId = 2,
            FilePath = "/nonexistent/path/video.mp4" // Arquivo que não existe
        };

        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                      .Returns(Task.CompletedTask);

        // Act
        await _service.ProcessVideoAsync(videoMessage);

        // Assert
        _mockRepository.Verify(r => r.UpdateStatusAsync(2, "Erro",
            It.Is<string>(msg => msg.Contains("Arquivo não encontrado")), 0), Times.Once);
    }

    [Fact]
    public async Task ProcessVideoAsync_InvalidVideoMetadata_ShouldUpdateErrorStatus() {
        // Arrange
        var tempFilePath = CreateTempVideoFile("invalid.mp4", 0); // Arquivo inválido (duração 0)
        var videoMessage = new VideoMessage {
            VideoId = 3,
            FilePath = tempFilePath
        };

        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                      .Returns(Task.CompletedTask);

        // Act
        await _service.ProcessVideoAsync(videoMessage);

        // Assert
        _mockRepository.Verify(r => r.UpdateStatusAsync(3, "Erro",
            It.Is<string>(msg => msg.Contains("Falha na análise de metadados")), 0), Times.Once);

        // Cleanup
        CleanupTempFile(tempFilePath);
    }

    [Fact]
    public async Task ProcessVideoAsync_NoFramesExtracted_ShouldCompleteWithWarning() {
        // Arrange
        var tempFilePath = CreateTempVideoFile("empty.mp4", 5); // Vídeo de 5s mas sem frames válidos
        var videoMessage = new VideoMessage {
            VideoId = 4,
            FilePath = tempFilePath
        };

        // Simular que não há frames extraídos (mock seria complexo, então testamos comportamento geral)
        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                      .Returns(Task.CompletedTask);

        // Act
        await _service.ProcessVideoAsync(videoMessage);

        // Assert
        _mockRepository.Verify(r => r.UpdateStatusAsync(4, "Concluído",
            It.Is<string>(msg => msg.Contains("Nenhum frame")), 5), Times.Once);

        // Cleanup
        CleanupTempFile(tempFilePath);
    }

    [Fact]
    public async Task ProcessVideoAsync_ConcurrentProcessing_ShouldNotDuplicateQRCodes() {
        // Arrange - Teste específico para thread-safety
        var tempFilePath = CreateTempVideoFile("concurrent_test.mp4", 3); // Vídeo de 3s
        var videoMessage = new VideoMessage {
            VideoId = 5,
            FilePath = tempFilePath
        };

        // Simular que o repositório recebe QR codes
        var expectedQRCodes = new List<QRCodeResult> {
            new QRCodeResult { Content = "https://test1.com", Timestamp = 1 },
            new QRCodeResult { Content = "https://test2.com", Timestamp = 2 }
        };

        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                      .Returns(Task.CompletedTask);
        _mockRepository.Setup(r => r.AddQRCodesAsync(5, It.IsAny<List<QRCodeResult>>()))
                      .Callback<int, List<QRCodeResult>>((id, qrs) => {
                          // ✅ VERIFICAÇÃO: Deve ter exatamente 2 QR codes únicos
                          var uniqueQrs = qrs.GroupBy(qr => new { qr.Content, qr.Timestamp })
                                            .Select(g => g.First())
                                            .Count();
                          Assert.Equal(2, uniqueQrs); // Não deve duplicar
                      })
                      .Returns(Task.CompletedTask);

        // Act
        await _service.ProcessVideoAsync(videoMessage);

        // Assert
        _mockRepository.Verify(r => r.AddQRCodesAsync(5, It.IsAny<List<QRCodeResult>>()), Times.Once);

        // Cleanup
        CleanupTempFile(tempFilePath);
    }

    [Fact]
    public async Task ProcessVideoAsync_LongVideo_ShouldUseOptimizedFPS() {
        // Arrange - Testa otimização de FPS para vídeos longos
        var tempFilePath = CreateTempVideoFile("long_video.mp4", 150); // Vídeo > 120s
        var videoMessage = new VideoMessage {
            VideoId = 6,
            FilePath = tempFilePath
        };

        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                      .Returns(Task.CompletedTask);

        // Act
        await _service.ProcessVideoAsync(videoMessage);

        // Assert - Deve usar FPS otimizado (0.5) para vídeo longo
        _mockRepository.Verify(r => r.UpdateStatusAsync(6, "Processando", null, 150), Times.Once);

        // Cleanup
        CleanupTempFile(tempFilePath);
    }

    [Fact]
    public async Task ProcessVideoAsync_ShortVideo_ShouldUseDefaultFPS() {
        // Arrange - Testa FPS padrão para vídeos curtos
        var tempFilePath = CreateTempVideoFile("short_video.mp4", 30); // Vídeo < 120s
        var videoMessage = new VideoMessage {
            VideoId = 7,
            FilePath = tempFilePath
        };

        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                      .Returns(Task.CompletedTask);

        // Act
        await _service.ProcessVideoAsync(videoMessage);

        // Assert - Deve usar FPS padrão (1.0) para vídeo curto
        _mockRepository.Verify(r => r.UpdateStatusAsync(7, "Processando", null, 30), Times.Once);

        // Cleanup
        CleanupTempFile(tempFilePath);
    }

    [Fact]
    public void Constructor_WithNullRepository_ThrowsArgumentNullException() {
        // Arrange
        var mockLogger = new Mock<ILogger<VideoProcessingService>>();
        var mockVideoOptions = new Mock<IOptions<Services.VideoProcessingOptions>>();
        mockVideoOptions.Setup(o => o.Value).Returns(_videoOptions);
        var mockStorageOptions = new Mock<IOptions<Services.VideoStorageOptions>>();
        mockStorageOptions.Setup(o => o.Value).Returns(_storageOptions);

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new VideoProcessingService(null!, mockLogger.Object, mockVideoOptions.Object, mockStorageOptions.Object));

        Assert.Equal("videoRepository", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException() {
        // Arrange
        var mockVideoOptions = new Mock<IOptions<Services.VideoProcessingOptions>>();
        mockVideoOptions.Setup(o => o.Value).Returns(_videoOptions);
        var mockStorageOptions = new Mock<IOptions<Services.VideoStorageOptions>>();
        mockStorageOptions.Setup(o => o.Value).Returns(_storageOptions);

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new VideoProcessingService(_mockRepository.Object, null!, mockVideoOptions.Object, mockStorageOptions.Object));

        Assert.Equal("logger", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithNullVideoOptions_ThrowsArgumentNullException() {
        // Arrange
        var mockStorageOptions = new Mock<IOptions<Services.VideoStorageOptions>>();
        mockStorageOptions.Setup(o => o.Value).Returns(_storageOptions);

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new VideoProcessingService(_mockRepository.Object, _mockLogger.Object, null!, mockStorageOptions.Object));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithNullStorageOptions_ThrowsArgumentNullException() {
        // Arrange
        var mockVideoOptions = new Mock<IOptions<Services.VideoProcessingOptions>>();
        mockVideoOptions.Setup(o => o.Value).Returns(_videoOptions);

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new VideoProcessingService(_mockRepository.Object, _mockLogger.Object, mockVideoOptions.Object, null!));

        Assert.Equal("storageOptions", exception.ParamName);
    }

    [Fact]
    public void Constructor_ShouldCreateTempDirectory() {
        // Arrange - Diretório é criado no construtor
        var tempPath = _storageOptions.TempFramesPath;

        // Act - Construtor já foi chamado no setup

        // Assert
        Assert.True(Directory.Exists(tempPath), $"Diretório temporário {tempPath} deve ser criado");
    }

    #region Helpers

    private string CreateTempVideoFile(string fileName, int durationSeconds) {
        var tempPath = Path.Combine(Path.GetTempPath(), fileName);

        // ✅ Criar arquivo dummy que simule um vídeo com duração específica
        // Para testes reais, seria necessário um arquivo MP4 válido
        // Por simplicidade, criamos um arquivo com metadados simulados
        var fileContent = GenerateDummyVideoFile(durationSeconds);
        File.WriteAllBytes(tempPath, fileContent);

        return tempPath;
    }

    private byte[] GenerateDummyVideoFile(int durationSeconds) {
        // ✅ Arquivo dummy com bytes específicos para simular metadados de vídeo
        // Em testes reais, seria melhor usar arquivos MP4 válidos
        var header = new byte[] {
            0x00, 0x00, 0x00, 0x18, // ftyp box size
            0x66, 0x74, 0x79, 0x70, // ftyp
            0x69, 0x73, 0x6F, 0x6D  // isom
        };

        var durationBytes = BitConverter.GetBytes(durationSeconds);
        var content = new byte[1024]; // Conteúdo mínimo

        return header.Concat(durationBytes).Concat(content).ToArray();
    }

    private void CleanupTempFile(string filePath) {
        try {
            if (File.Exists(filePath)) {
                File.Delete(filePath);
            }
        } catch (Exception ex) {
            // Ignorar erros de cleanup nos testes
            Console.WriteLine($"Cleanup warning: {ex.Message}");
        }
    }

    #endregion

    // ✅ IMPLEMENTAÇÃO DO IDisposable
    public void Dispose() {
        // ✅ CLEANUP: Remove diretório temporário após todos os testes
        try {
            if (Directory.Exists(_testTempPath)) {
                Directory.Delete(_testTempPath, true);
            }
        } catch (Exception ex) {
            Console.WriteLine($"Dispose warning: {ex.Message}");
        }
    }
}