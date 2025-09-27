using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using ScanForge.Controllers;
using System;
using Xunit;

namespace ScanForge.UnitTests.Controllers;

public class HealthControllerTests {
    private readonly Mock<ILogger<HealthController>> _mockLogger;
    private readonly HealthController _controller;

    public HealthControllerTests() {
        _mockLogger = new Mock<ILogger<HealthController>>();
        _controller = new HealthController(_mockLogger.Object);
    }

    [Fact]
    public void Constructor_NullLogger_ShouldThrowArgumentNullException() {
        // Act & Assert
        Action act = () => new HealthController(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void Get_ShouldReturnOkWithHealthyStatus() {
        // Act
        var result = _controller.Get() as OkObjectResult;

        // Assert
        result.Should().NotBeNull();
        result.StatusCode.Should().Be(200);

        // Definir o tipo esperado manualmente
        var response = result.Value as object; // Cast para object primeiro
        var statusProperty = response?.GetType().GetProperty("status");
        var timestampProperty = response?.GetType().GetProperty("timestamp");
        var serviceProperty = response?.GetType().GetProperty("service");

        statusProperty.Should().NotBeNull();
        timestampProperty.Should().NotBeNull();
        serviceProperty.Should().NotBeNull();

        statusProperty.GetValue(response).Should().Be("Healthy");
        serviceProperty.GetValue(response).Should().Be("ScanForge Worker");

        // Ajuste para DateTime com cast explícito
        var timestampValue = (DateTime?)timestampProperty.GetValue(response);
        timestampValue.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));

        _mockLogger.Verify(l => l.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Health check executado")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }
}