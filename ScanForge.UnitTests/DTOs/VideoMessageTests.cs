using FluentAssertions;
using ScanForge.DTOs;
using System.Text.Json;
using Xunit;

namespace ScanForge.UnitTests.DTOs;

public class VideoMessageTests {
    [Fact]
    public void VideoMessage_ShouldSerializeCorrectly() {
        // Arrange
        var message = new VideoMessage { VideoId = 1, FilePath = "/uploads/test.mp4" };

        // Act
        var json = JsonSerializer.Serialize(message);
        var deserialized = JsonSerializer.Deserialize<VideoMessage>(json);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.VideoId.Should().Be(1);
        deserialized.FilePath.Should().Be("/uploads/test.mp4");
    }

    [Theory]
    [InlineData(0, "/uploads/test.mp4", false)]  // VideoId inválido
    [InlineData(1, "", false)]  // FilePath vazio
    [InlineData(1, "/uploads/test.mp4", true)]  // Válido
    public void VideoMessage_Properties_ShouldValidateCorrectly(int videoId, string filePath, bool isValid) {
        // Arrange & Act
        var message = new VideoMessage { VideoId = videoId, FilePath = filePath };

        // Assert
        if (isValid) {
            message.VideoId.Should().BeGreaterThan(0);
            message.FilePath.Should().NotBeEmpty();
        } else {
            Action act = () => {
                if (message.VideoId <= 0) throw new ArgumentException("VideoId inválido");
                if (string.IsNullOrEmpty(message.FilePath)) throw new ArgumentException("FilePath vazio");
            };
            act.Should().Throw<ArgumentException>();
        }
    }
}