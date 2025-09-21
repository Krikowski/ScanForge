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
using System.Threading.Tasks;
using Xunit;

namespace ScanForge.Tests.Unit;

public class VideoProcessingServiceTests {
    private readonly Mock<IVideoRepository> _mockRepository;
    private readonly Mock<ILogger<VideoProcessingService>> _mockLogger;
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly VideoProcessingService _service;

    public VideoProcessingServiceTests() {
        _mockRepository = new Mock<IVideoRepository>();
        _mockLogger = new Mock<ILogger<VideoProcessingService>>();
        _mockConfiguration = new Mock<IConfiguration>();

        // Configuração mock para testes
        SetupConfigurationMock();

        // Correção: Construtor com 3 parâmetros (Repository, Logger, Configuration)
        _service = new VideoProcessingService(
            _mockRepository.Object,
            _mockLogger.Object,
            _mockConfiguration.Object);
    }

    private void SetupConfigurationMock() {
        // Mock das configurações necessárias
        _mockConfiguration.Setup(c => c["VideoStorage:BasePath"]).Returns("/app/uploads");
        _mockConfiguration.Setup(c => c["VideoStorage:TempFramesPath"]).Returns("/tmp/frames");
        _mockConfiguration.Setup(c => c["Optimization:DurationThreshold"]).Returns("120");
        _mockConfiguration.Setup(c => c["Optimization:OptimizedFps"]).Returns("0.5");
        _mockConfiguration.Setup(c => c["Optimization:DefaultFps"]).Returns("1.0");
    }

    [Fact]
    public async Task ProcessVideoAsync_ValidVideo_ShouldUpdateStatusAndProcessFrames() {
        // Arrange
        var videoMessage = new VideoMessage {
            VideoId = 1,
            FilePath = CreateTempFile("test.mp4")
        };

        var fakeVideo = new VideoResult { VideoId = 1, Title = "Test Video", Status = "Na Fila" };

        _mockRepository.Setup(r => r.GetVideoByIdAsync(1)).ReturnsAsync(fakeVideo);
        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                      .Returns(Task.CompletedTask);
        _mockRepository.Setup(r => r.AddQRCodesAsync(It.IsAny<int>(), It.IsAny<List<QRCodeResult>>()))
                      .Returns(Task.CompletedTask);

        // Act
        await _service.ProcessVideoAsync(videoMessage);

        // Assert
        _mockRepository.Verify(r => r.UpdateStatusAsync(1, "Processando", null, It.IsAny<int>()), Times.Once);
        _mockRepository.Verify(r => r.UpdateStatusAsync(1, "Concluído", null, 0), Times.Once);
        _mockRepository.Verify(r => r.AddQRCodesAsync(1, It.IsAny<List<QRCodeResult>>()), Times.Once);
    }

    [Fact]
    public async Task ProcessVideoAsync_FileNotFound_ShouldUpdateErrorStatus() {
        // Arrange
        var videoMessage = new VideoMessage {
            VideoId = 2,
            FilePath = "/app/uploads/nonexistent.mp4"
        };

        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                      .Returns(Task.CompletedTask);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.ProcessVideoAsync(videoMessage));

        Assert.Contains("não encontrado", exception.Message);

        _mockRepository.Verify(r => r.UpdateStatusAsync(2, "Erro", It.Is<string>(s => s.Contains("não encontrado")), 0), Times.Once);
    }

    [Fact]
    public async Task ProcessVideoAsync_ExceptionInProcessing_ShouldUpdateErrorStatus() {
        // Arrange
        var videoMessage = new VideoMessage {
            VideoId = 3,
            FilePath = CreateTempFile("error.mp4")
        };

        // Simula exceção no repositório
        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), "Processando", null, It.IsAny<int>()))
                      .ThrowsAsync(new InvalidOperationException("Database error"));

        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                      .Returns(Task.CompletedTask);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ProcessVideoAsync(videoMessage));

        Assert.Contains("Falha processamento 3", exception.Message);
        _mockRepository.Verify(r => r.UpdateStatusAsync(3, "Erro", It.IsAny<string>(), 0), Times.Once);
    }

    [Fact]
    public void Constructor_WithNullRepository_ThrowsArgumentNullException() {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new VideoProcessingService(null!, _mockLogger.Object, _mockConfiguration.Object));
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException() {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new VideoProcessingService(_mockRepository.Object, null!, _mockConfiguration.Object));
    }

    [Fact]
    public void Constructor_WithNullConfiguration_ThrowsArgumentNullException() {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new VideoProcessingService(_mockRepository.Object, _mockLogger.Object, null!));
    }

    private string CreateTempFile(string fileName) {
        var tempPath = Path.Combine(Path.GetTempPath(), fileName);
        File.WriteAllBytes(tempPath, new byte[] { 0x00, 0x00 }); // Arquivo dummy
        return tempPath;
    }
}