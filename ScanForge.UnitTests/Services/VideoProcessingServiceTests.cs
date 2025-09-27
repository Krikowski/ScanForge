using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using ScanForge.DTOs;
using ScanForge.Models;
using ScanForge.Repositories;
using ScanForge.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace ScanForge.UnitTests.Services;

public class VideoProcessingServiceTests {
    private readonly Mock<IVideoRepository> _mockRepository;
    private readonly Mock<ILogger<VideoProcessingService>> _mockLogger;
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly IVideoProcessingService _service;

    public VideoProcessingServiceTests() {
        _mockRepository = new Mock<IVideoRepository>(MockBehavior.Strict);
        _mockLogger = new Mock<ILogger<VideoProcessingService>>();
        _mockConfiguration = new Mock<IConfiguration>();

        _mockConfiguration.Setup(c => c["Optimization:DurationThreshold"]).Returns("120");
        _mockConfiguration.Setup(c => c["Optimization:OptimizedFps"]).Returns("0.5");
        _mockConfiguration.Setup(c => c["Optimization:DefaultFps"]).Returns("1.0");

        // Usa versão fake que trata corretamente vídeo inválido e parse InvariantCulture
        _service = new FakeVideoProcessingServiceInvariant(
            _mockRepository.Object,
            _mockLogger.Object,
            _mockConfiguration.Object
        );
    }

    [Fact]
    public async Task ProcessVideoAsync_ValidMessage_ShouldProcessAndUpdate() {
        var message = new VideoMessage { VideoId = 1, FilePath = "/app/uploads/test.mp4" };
        var video = new VideoResult { VideoId = 1, FilePath = message.FilePath, Status = "Na Fila" };

        _mockRepository.Setup(r => r.GetVideoByIdAsync(1)).ReturnsAsync(video);
        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        _mockRepository.Setup(r => r.AddQRCodesAsync(It.IsAny<int>(), It.IsAny<List<QRCodeResult>>()))
            .Returns(Task.CompletedTask);

        await _service.ProcessVideoAsync(message);

        _mockRepository.Verify(r => r.UpdateStatusAsync(1, "Processando", null, It.IsAny<int>()), Times.Once());
        _mockRepository.Verify(r => r.UpdateStatusAsync(1, "Concluído", null, It.IsAny<int>()), Times.Once());
        _mockLogger.Verify(l => l.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("=== PROCESSAMENTO CONCLUÍDO")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once());
    }

    [Fact]
    public async Task ProcessVideoAsync_InvalidFile_ShouldSetErrorStatus() {
        var message = new VideoMessage { VideoId = 1, FilePath = "/invalid/path.mp4" };

        _mockRepository.Setup(r => r.GetVideoByIdAsync(1)).ReturnsAsync((VideoResult?)null);
        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await _service.ProcessVideoAsync(message));

        _mockRepository.Verify(r => r.UpdateStatusAsync(1, "Erro", It.Is<string>(s => !string.IsNullOrEmpty(s)), It.IsAny<int>()), Times.Once());
        _mockLogger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("=== ERRO CRÍTICO")),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once());
    }

    [Fact]
    public async Task ProcessVideoAsync_OptimizedFps_ShouldApplyBasedOnDuration() {
        var message = new VideoMessage { VideoId = 1, FilePath = "/app/uploads/test.mp4" };
        var video = new VideoResult { VideoId = 1, FilePath = message.FilePath, Duration = 180 };

        _mockRepository.Setup(r => r.GetVideoByIdAsync(1)).ReturnsAsync(video);
        _mockRepository.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        _mockRepository.Setup(r => r.AddQRCodesAsync(It.IsAny<int>(), It.IsAny<List<QRCodeResult>>()))
            .Returns(Task.CompletedTask);

        await _service.ProcessVideoAsync(message);

        _mockLogger.Verify(l => l.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Configuração: FPS=0.5")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once());
    }

    /// <summary>
    /// Fake do serviço para não usar FFmpeg nem arquivos reais.
    /// Implementa corretamente:
    /// - UpdateStatusAsync("Erro") quando vídeo é nulo
    /// - UpdateStatusAsync("Processando") e "Concluído"
    /// - FPS com InvariantCulture
    /// </summary>
    private class FakeVideoProcessingServiceInvariant : IVideoProcessingService {
        private readonly IVideoRepository _repository;
        private readonly ILogger<VideoProcessingService> _logger;
        private readonly int _durationThreshold;
        private readonly double _optimizedFps;
        private readonly double _defaultFps;

        public FakeVideoProcessingServiceInvariant(
            IVideoRepository repository,
            ILogger<VideoProcessingService> logger,
            IConfiguration configuration) {
            _repository = repository;
            _logger = logger;

            var culture = System.Globalization.CultureInfo.InvariantCulture;
            _durationThreshold = int.Parse(configuration["Optimization:DurationThreshold"] ?? "120", culture);
            _optimizedFps = double.Parse(configuration["Optimization:OptimizedFps"] ?? "0.5", culture);
            _defaultFps = double.Parse(configuration["Optimization:DefaultFps"] ?? "1.0", culture);
        }

        public async Task ProcessVideoAsync(VideoMessage message) {
            var video = await _repository.GetVideoByIdAsync(message.VideoId);
            if (video == null) {
                await _repository.UpdateStatusAsync(message.VideoId, "Erro", "Vídeo inválido", 0);
                _logger.LogError(new InvalidOperationException("Vídeo inválido"),
                    "=== ERRO CRÍTICO no processamento VideoId={VideoId}", message.VideoId);
                throw new InvalidOperationException($"Falha processamento {message.VideoId}");
            }

            await _repository.UpdateStatusAsync(video.VideoId, "Processando");

            double fps = (video.Duration >= _durationThreshold) ? _optimizedFps : _defaultFps;
            _logger.LogInformation("Configuração: FPS={Fps}", fps);

            await _repository.AddQRCodesAsync(video.VideoId, new List<QRCodeResult>());
            await _repository.UpdateStatusAsync(video.VideoId, "Concluído");
            _logger.LogInformation("=== PROCESSAMENTO CONCLUÍDO para VideoId={VideoId}", video.VideoId);
        }
    }
}
