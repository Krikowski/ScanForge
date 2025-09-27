using FluentAssertions;
using ScanForge.Models;
using Xunit;

namespace ScanForge.UnitTests.Models;

public class VideoResultTests {
    [Fact]
    public void VideoResult_DefaultProperties_ShouldBeSetCorrectly() {
        // Arrange & Act
        var result = new VideoResult();

        // Assert
        result.VideoId.Should().Be(0);
        result.Title.Should().BeEmpty();
        result.Description.Should().BeNull();
        result.FilePath.Should().BeNull();
        result.Status.Should().Be("Na Fila");
        result.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        result.Duration.Should().Be(0);
        result.ErrorMessage.Should().BeNull();
        result.QRCodes.Should().BeEmpty();
        result.LastUpdated.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void QRCodeResult_Properties_ShouldBeSetCorrectly() {
        // Arrange
        var qr = new QRCodeResult { Content = "https://example.com", Timestamp = 10 };

        // Assert
        qr.Content.Should().Be("https://example.com");
        qr.Timestamp.Should().Be(10);
    }

    [Theory]
    [InlineData("Na Fila", true)]
    [InlineData("Processando", true)]
    [InlineData("Concluído", true)]
    [InlineData("Erro", true)]
    [InlineData("Inválido", false)]
    public void VideoResult_Status_ShouldValidateAgainstValidStatuses(string status, bool isValid) {
        // Arrange
        var result = new VideoResult { Status = status };

        // Assert
        var validStatuses = new[] { "Na Fila", "Processando", "Concluído", "Erro" };
        if (isValid)
            validStatuses.Should().Contain(result.Status);
        else
            validStatuses.Should().NotContain(result.Status);
    }
}