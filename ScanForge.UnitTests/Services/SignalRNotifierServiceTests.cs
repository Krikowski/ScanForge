using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using ScanForge.Models;
using ScanForge.Services;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ScanForge.UnitTests.Services;

public class SignalRNotifierServiceTests {
    private readonly Mock<ILogger<SignalRNotifierService>> _mockLogger;
    private readonly IConfiguration _configuration;

    public SignalRNotifierServiceTests() {
        _mockLogger = new Mock<ILogger<SignalRNotifierService>>();

        var configData = new Dictionary<string, string?>
        {
            { "SignalR:HubUrl", "http://videonest_service:8080/videoHub" },
            { "SignalR:MaxRetries", "3" },
            { "SignalR:RetryDelayMs", "100" } // reduzir para acelerar testes
        };
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();
    }

    private void SetHubConnection(SignalRNotifierService service, IHubConnectionWrapper connection) {
        var hubConnectionField = service.GetType()
            .GetField("_hubConnection", BindingFlags.NonPublic | BindingFlags.Instance);
        hubConnectionField?.SetValue(service, connection);
    }

    private void SetHttpClient(SignalRNotifierService service, HttpClient client) {
        var httpClientField = service.GetType()
            .GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance);
        httpClientField?.SetValue(service, client);
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException() {
        Action act = () => new SignalRNotifierService(_configuration, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public async Task NotifyCompletionAsync_ValidVideo_ShouldSendMessage() {
        var video = new VideoResult { VideoId = 1, Status = "Concluído", QRCodes = new List<QRCodeResult>() };
        var mockConnection = new Mock<IHubConnectionWrapper>();
        mockConnection.Setup(c => c.State).Returns(HubConnectionState.Connected);
        mockConnection.Setup(c => c.InvokeAsync("VideoProcessed", It.IsAny<VideoResult>())).Returns(Task.CompletedTask);

        var service = new SignalRNotifierService(_configuration, _mockLogger.Object);
        SetHubConnection(service, mockConnection.Object);

        await service.NotifyCompletionAsync(video);

        mockConnection.Verify(c => c.InvokeAsync("VideoProcessed", It.Is<VideoResult>(v => v.VideoId == video.VideoId)), Times.Once);
        _mockLogger.Verify(l => l.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("VideoId=1 notificado")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task NotifyCompletionAsync_ConnectionFailed_ShouldLogWarningAndRetry() {
        var video = new VideoResult { VideoId = 1 };
        var mockConnection = new Mock<IHubConnectionWrapper>();
        mockConnection.Setup(c => c.State).Returns(HubConnectionState.Connected);
        mockConnection.SetupSequence(c => c.InvokeAsync("VideoProcessed", It.IsAny<VideoResult>()))
            .ThrowsAsync(new HubException("Falha"))
            .ThrowsAsync(new HubException("Falha"))
            .ThrowsAsync(new HubException("Falha"));

        var service = new SignalRNotifierService(_configuration, _mockLogger.Object);
        SetHubConnection(service, mockConnection.Object);

        await service.NotifyCompletionAsync(video);

        _mockLogger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Todas as 3 tentativas") || v.ToString()!.Contains("Todas as 3 tentativas falharam")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task NotifyCompletionAsync_Connected_FailsTwiceThenSucceeds_CallsInvokeAsyncWithRetries() {
        var video = new VideoResult { VideoId = 1 };
        var mockConnection = new Mock<IHubConnectionWrapper>();
        mockConnection.Setup(c => c.State).Returns(HubConnectionState.Connected);
        mockConnection.SetupSequence(c => c.InvokeAsync("VideoProcessed", It.IsAny<VideoResult>()))
            .ThrowsAsync(new HubException("Falha"))
            .ThrowsAsync(new HubException("Falha"))
            .Returns(Task.CompletedTask);

        var service = new SignalRNotifierService(_configuration, _mockLogger.Object);
        SetHubConnection(service, mockConnection.Object);

        await service.NotifyCompletionAsync(video);

        // Verifica que foi chamado 3 vezes (2 falhas + 1 sucesso)
        mockConnection.Verify(c => c.InvokeAsync("VideoProcessed", It.Is<VideoResult>(v => v.VideoId == video.VideoId)), Times.Exactly(3));
    }

    [Fact]
    public async Task NotifyCompletionAsync_Connected_AllRetriesFail_CallsFallbackHttp() {
        var video = new VideoResult { VideoId = 1 };

        var mockConnection = new Mock<IHubConnectionWrapper>();
        mockConnection.Setup(c => c.State).Returns(HubConnectionState.Connected);

        // IMPORTANTE: usar HubException para acionar o fluxo de retry (e fallback no último attempt)
        mockConnection.Setup(c => c.InvokeAsync("VideoProcessed", It.IsAny<VideoResult>()))
                      .ThrowsAsync(new HubException("Falha"));

        var service = new SignalRNotifierService(_configuration, _mockLogger.Object);
        SetHubConnection(service, mockConnection.Object);

        // Mock do HttpMessageHandler para interceptar PutAsync do HttpClient
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        var httpClient = new HttpClient(mockHandler.Object);
        SetHttpClient(service, httpClient);

        await service.NotifyCompletionAsync(video);

        // Verifica que o fallback HTTP foi chamado (URL com /api/videos/1/status)
        mockHandler.Protected().Verify(
            "SendAsync",
            Times.AtLeastOnce(),
            ItExpr.Is<HttpRequestMessage>(m => m.RequestUri!.ToString().Contains("/api/videos/1/status")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task NotifyProcessingStartedAsync_Connected_Success_CallsInvokeAsync() {
        var videoId = 1;
        var title = "Test Video";
        var mockConnection = new Mock<IHubConnectionWrapper>();
        mockConnection.Setup(c => c.State).Returns(HubConnectionState.Connected);

        object? capturedArg = null;
        mockConnection.Setup(c => c.InvokeAsync("VideoProcessingStarted", It.IsAny<object>()))
            .Callback<string, object?>((method, arg) => capturedArg = arg)
            .Returns(Task.CompletedTask);

        var service = new SignalRNotifierService(_configuration, _mockLogger.Object);
        SetHubConnection(service, mockConnection.Object);

        await service.NotifyProcessingStartedAsync(videoId, title);

        // Asserção fora da expression tree (evita CS0854)
        capturedArg.Should().NotBeNull();
        var expectedJson = JsonSerializer.Serialize(new { VideoId = videoId, Title = title });
        JsonSerializer.Serialize(capturedArg).Should().Be(expectedJson);

        mockConnection.Verify(c => c.InvokeAsync("VideoProcessingStarted", It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task NotifyProcessingErrorAsync_NotConnected_SkipsNotification() {
        var videoId = 1;
        var error = "Error";
        var mockConnection = new Mock<IHubConnectionWrapper>();
        mockConnection.Setup(c => c.State).Returns(HubConnectionState.Disconnected);

        var service = new SignalRNotifierService(_configuration, _mockLogger.Object);
        SetHubConnection(service, mockConnection.Object);

        await service.NotifyProcessingErrorAsync(videoId, error);

        mockConnection.Verify(c => c.InvokeAsync(It.IsAny<string>(), It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task NotifyProcessingErrorAsync_Connected_Success_CallsInvokeAsync() {
        var videoId = 1;
        var error = "Processing failed";
        var mockConnection = new Mock<IHubConnectionWrapper>();
        mockConnection.Setup(c => c.State).Returns(HubConnectionState.Connected);

        object? capturedArg = null;
        mockConnection.Setup(c => c.InvokeAsync("VideoProcessingError", It.IsAny<object>()))
            .Callback<string, object?>((method, arg) => capturedArg = arg)
            .Returns(Task.CompletedTask);

        var service = new SignalRNotifierService(_configuration, _mockLogger.Object);
        SetHubConnection(service, mockConnection.Object);

        await service.NotifyProcessingErrorAsync(videoId, error);

        // Verifica payload via JSON fora da expression tree
        capturedArg.Should().NotBeNull();
        var expectedJson = JsonSerializer.Serialize(new { VideoId = videoId, Error = error });
        JsonSerializer.Serialize(capturedArg).Should().Be(expectedJson);

        mockConnection.Verify(c => c.InvokeAsync("VideoProcessingError", It.IsAny<object>()), Times.Once);

        _mockLogger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Erro de processamento notificado")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }
}
