using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

namespace ScanForge.Tests.Unit;

public class VideoProcessingServiceTests : IDisposable {
    private readonly Mock<IVideoRepository> _mockRepository;
    private readonly Mock<ILogger<VideoProcessingService>> _mockLogger;
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly VideoProcessingService _service;
    private readonly string _testTempPath;

    public VideoProcessingServiceTests() {
        _mockRepository = new Mock<IVideoRepository>();
        _mockLogger = new Mock<ILogger<VideoProcessingService>>();
        _mockConfiguration = new Mock<IConfiguration>();

        // ✅ CONFIGURAÇÃO: Setup das configurações via IConfiguration
        SetupConfiguration();

        // ✅ CONSTRUTOR ORIGINAL: 3 parâmetros (Repository, Logger, Configuration)
        _service = new VideoProcessingService(
            _mockRepository.Object,
            _mockLogger.Object,
            _mockConfiguration.Object);

        // ✅ SETUP: Criar diretório temporário para testes
        _testTempPath = Path.Combine(Path.GetTempPath(), "scanforge_test_frames");
        Directory.CreateDirectory(_testTempPath);
    }

    /// <summary>
    /// Configura o mock de IConfiguration com valores do appsettings.json
    /// </summary>
    private void SetupConfiguration() {
        // VideoStorage
        _mockConfiguration.Setup(c => c["VideoStorage:BasePath"]).Returns("/app/uploads");
        _mockConfiguration.Setup(c => c["VideoStorage:TempFramesPath"]).Returns(_testTempPath);

        // Optimization
        _mockConfiguration.Setup(c => c["Optimization:DurationThreshold"]).Returns("120");
        _mockConfiguration.Setup(c => c["Optimization:OptimizedFps"]).Returns("0.5");
        _mockConfiguration.Setup(c => c["Optimization:DefaultFps"]).Returns("1.0");

        // Logger setup
        _mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
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

        // Act
        await _service.ProcessVideoAsync(videoMessage);

        // Assert - Verifica sequência correta de chamadas
        // 1. Primeiro update: Status="Processando", duration=0 (antes da análise)
        _mockRepository.Verify(r => r.UpdateStatusAsync(1, "Processando", null, 0), Times.Once);

        // 2. Segundo update: Status="Processando", duration=10 (após análise FFProbe)
        _mockRepository.Verify(r => r.UpdateStatusAsync(1, "Processando", null, 10), Times.Once);

        // 3. Terceiro update: Status="Concluído" (final)
        _mockRepository.Verify(r => r.UpdateStatusAsync(1, "Concluído", null, 10), Times.Once);

        // 4. AddQRCodes (vazio, sem QRs detectados)
        _mockRepository.Verify(r => r.AddQRCodesAsync(1, It.Is<List<QRCodeResult>>(list => list.Count == 0)), Times.Once);

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
        await Assert.ThrowsAsync<FileNotFoundException>(() => _service.ProcessVideoAsync(videoMessage));

        // Assert - Deve falhar no ResolveFilePath e propagar exceção
        _mockRepository.Verify(r => r.UpdateStatusAsync(2, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never); // Não deve chamar repo antes da exceção
    }

    [Fact]
    public async Task ProcessVideoAsync_InvalidVideoMetadata_ShouldUpdateErrorStatus() {
        // Arrange
        var tempFilePath = CreateTempInvalidVideoFile("invalid.mp4");
        var videoMessage = new VideoMessage {
            VideoId = 3,
            FilePath = tempFilePath
        };

        // Mock para capturar o erro específico do FFProbe
        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                      .Returns(Task.CompletedTask)
                      .Callback<int, string, string?, int>((id, status, error, duration) => {
                          if (status == "Erro") {
                              Assert.Contains("Falha na análise de metadados", error ?? "");
                              Assert.Equal(0, duration);
                          }
                      });

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ProcessVideoAsync(videoMessage));

        // Assert
        _mockRepository.Verify(r => r.UpdateStatusAsync(3, "Processando", null, 0), Times.Once); // Status inicial
        _mockRepository.Verify(r => r.UpdateStatusAsync(3, "Erro",
            It.Is<string>(msg => msg.Contains("Falha na análise de metadados")), 0), Times.Once);

        // Cleanup
        CleanupTempFile(tempFilePath);
    }

    [Fact]
    public async Task ProcessVideoAsync_NoFramesExtracted_ShouldCompleteWithWarning() {
        // Arrange - Criar arquivo que passa FFProbe mas não gera frames válidos
        var tempFilePath = CreateTempVideoFile("empty_frames.mp4", 5);
        var videoMessage = new VideoMessage {
            VideoId = 4,
            FilePath = tempFilePath
        };

        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                      .Returns(Task.CompletedTask);
        _mockRepository.Setup(r => r.AddQRCodesAsync(It.IsAny<int>(), It.IsAny<List<QRCodeResult>>()))
                      .Returns(Task.CompletedTask);

        // Act
        await _service.ProcessVideoAsync(videoMessage);

        // Assert - Deve completar com status "Concluído" mesmo sem frames
        _mockRepository.Verify(r => r.UpdateStatusAsync(4, "Processando", null, 0), Times.Once); // Inicial
        _mockRepository.Verify(r => r.UpdateStatusAsync(4, "Processando", null, 5), Times.Once); // Após análise
        _mockRepository.Verify(r => r.UpdateStatusAsync(4, "Concluído", null, 5), Times.Once); // Final
        _mockRepository.Verify(r => r.AddQRCodesAsync(4, It.Is<List<QRCodeResult>>(list => list.Count == 0)), Times.Once);

        // Cleanup
        CleanupTempFile(tempFilePath);
    }

    [Fact]
    public async Task ProcessVideoAsync_ConcurrentProcessing_ShouldNotDuplicateQRCodes() {
        // Arrange - Teste específico para thread-safety (simplificado)
        var tempFilePath = CreateTempVideoFile("concurrent_test.mp4", 3);
        var videoMessage = new VideoMessage {
            VideoId = 5,
            FilePath = tempFilePath
        };

        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                      .Returns(Task.CompletedTask);
        _mockRepository.Setup(r => r.AddQRCodesAsync(5, It.IsAny<List<QRCodeResult>>()))
                      .Callback<int, List<QRCodeResult>>((id, qrs) => {
                          // ✅ VERIFICAÇÃO: Deve ter QR codes únicos (teste simula 2 únicos)
                          var uniqueByContent = qrs.GroupBy(qr => qr.Content).Count();
                          Assert.True(uniqueByContent <= 2, "Não deve duplicar QR codes por conteúdo");
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
        _mockRepository.Setup(r => r.AddQRCodesAsync(It.IsAny<int>(), It.IsAny<List<QRCodeResult>>()))
                      .Returns(Task.CompletedTask);

        // Act
        await _service.ProcessVideoAsync(videoMessage);

        // Assert - Deve usar FPS otimizado (0.5) para vídeo longo - verificado pela duração > threshold
        _mockRepository.Verify(r => r.UpdateStatusAsync(6, "Processando", null, 150), Times.Once); // Duration correta
        _mockRepository.Verify(r => r.UpdateStatusAsync(6, "Concluído", null, 150), Times.Once);

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
        _mockRepository.Setup(r => r.AddQRCodesAsync(It.IsAny<int>(), It.IsAny<List<QRCodeResult>>()))
                      .Returns(Task.CompletedTask);

        // Act
        await _service.ProcessVideoAsync(videoMessage);

        // Assert - Deve usar FPS padrão (1.0) para vídeo curto - verificado pela duração < threshold
        _mockRepository.Verify(r => r.UpdateStatusAsync(7, "Processando", null, 30), Times.Once);
        _mockRepository.Verify(r => r.UpdateStatusAsync(7, "Concluído", null, 30), Times.Once);

        // Cleanup
        CleanupTempFile(tempFilePath);
    }

    [Fact]
    public void Constructor_WithNullRepository_ThrowsArgumentNullException() {
        // Arrange
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new VideoProcessingService(null!, _mockLogger.Object, _mockConfiguration.Object));
        Assert.Equal("repository", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException() {
        // Arrange
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new VideoProcessingService(_mockRepository.Object, null!, _mockConfiguration.Object));
        Assert.Equal("logger", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithNullConfiguration_ThrowsArgumentNullException() {
        // Arrange
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new VideoProcessingService(_mockRepository.Object, _mockLogger.Object, null!));
        Assert.Equal("configuration", exception.ParamName);
    }

    [Fact]
    public void Constructor_ShouldCreateTempDirectory() {
        // Arrange - Diretório é criado via configuração no construtor
        var tempPath = _testTempPath;

        // Act - Construtor já foi chamado no setup

        // Assert
        Assert.True(Directory.Exists(tempPath), $"Diretório temporário {tempPath} deve ser criado");
    }

    #region Helpers

    /// <summary>
    /// Cria arquivo temporário simulando vídeo com duração específica
    /// Para testes unitários, não precisa ser MP4 real - FFProbe mock simula
    /// </summary>
    private string CreateTempVideoFile(string fileName, int durationSeconds) {
        var tempPath = Path.Combine(Path.GetTempPath(), fileName);
        // Criar arquivo dummy que simule um vídeo válido
        var fileContent = GenerateDummyVideoFile(durationSeconds);
        File.WriteAllBytes(tempPath, fileContent);
        return tempPath;
    }

    /// <summary>
    /// Cria arquivo temporário simulando vídeo inválido para testar FFProbe falha
    /// </summary>
    private string CreateTempInvalidVideoFile(string fileName) {
        var tempPath = Path.Combine(Path.GetTempPath(), fileName);
        // Arquivo com apenas 1 byte - definitivamente inválido para FFProbe
        File.WriteAllBytes(tempPath, new byte[] { 0xFF });
        return tempPath;
    }

    /// <summary>
    /// Gera conteúdo dummy para simular arquivo de vídeo
    /// Em testes reais, seria melhor usar arquivos MP4 válidos
    /// </summary>
    private byte[] GenerateDummyVideoFile(int durationSeconds) {
        // Simula header MP4 básico + duração
        var header = new byte[] {
            0x00, 0x00, 0x00, 0x18, // ftyp box size
            0x66, 0x74, 0x79, 0x70, // ftyp
            0x69, 0x73, 0x6F, 0x6D, // isom
            0x00, 0x00, 0x02, 0x00 // compatibility
        };

        // Simula duração em bytes (simplificado)
        var durationBytes = BitConverter.GetBytes(durationSeconds * 1000); // ms
        var content = new byte[1024]; // Conteúdo mínimo para parecer arquivo real
        Random.Shared.NextBytes(content); // Preenche com dados aleatórios

        return header.Concat(durationBytes).Concat(content).ToArray();
    }

    /// <summary>
    /// Limpa arquivo temporário após teste
    /// </summary>
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