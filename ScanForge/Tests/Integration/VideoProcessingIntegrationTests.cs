using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using ScanForge.DTOs;
using ScanForge.Models;
using ScanForge.Repositories;
using ScanForge.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Testcontainers.MongoDb;
using Xunit;

namespace ScanForge.Tests.Integration;

public class VideoProcessingIntegrationTests : IAsyncLifetime {
    private readonly MongoDbContainer _mongoDbContainer;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _testVideoPath;
    private readonly IMongoDatabase _database;

    public VideoProcessingIntegrationTests() {
        _mongoDbContainer = new MongoDbBuilder().Build();

        var services = new ServiceCollection();

        // Configuração para testes de integração
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["VideoStorage:BasePath"] = "/app/uploads",
                ["VideoStorage:TempFramesPath"] = Path.Combine(Path.GetTempPath(), "scanforge_frames"),
                ["Optimization:DurationThreshold"] = "10",
                ["Optimization:OptimizedFps"] = "1.0",
                ["Optimization:DefaultFps"] = "1.0"
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(builder => builder.AddConsole());

        // Conecta ao MongoDB container
        services.AddSingleton<IMongoClient>(new MongoClient(_mongoDbContainer.GetConnectionString()));
        services.AddSingleton<IMongoDatabase>(sp =>
            sp.GetRequiredService<IMongoClient>().GetDatabase("Hackathon_FIAP_Test"));

        services.AddScoped<IVideoRepository, VideoRepository>();
        services.AddScoped<IVideoProcessingService, VideoProcessingService>();

        _serviceProvider = services.BuildServiceProvider();
        _database = _serviceProvider.GetRequiredService<IMongoDatabase>();

        // Cria arquivo de vídeo de teste
        _testVideoPath = CreateTestVideoFile();
    }

    /// <summary>
    /// Cria arquivo de vídeo de teste dummy para simular upload
    /// </summary>
    private string CreateTestVideoFile() {
        var testPath = Path.Combine(Path.GetTempPath(), "test_video.mp4");

        // Cria um arquivo dummy para simular vídeo (MP4 header básico)
        File.WriteAllBytes(testPath, new byte[] {
            0x00, 0x00, 0x00, 0x18, // MP4 box size
            0x66, 0x74, 0x79, 0x70, // ftyp atom
            0x69, 0x73, 0x6F, 0x6D, // isom brand
            0x00, 0x00, 0x00, 0x00, // minor version
            0x69, 0x73, 0x6F, 0x6D, // compatible brands
            0x61, 0x76, 0x63, 0x31  // avc1
        });

        return testPath;
    }

    /// <summary>
    /// Cria arquivo temporário para testes de vídeo
    /// Correção: Método adicionado para resolver erro CS0103
    /// </summary>
    private string CreateTempFile(string fileName) {
        var tempPath = Path.Combine(Path.GetTempPath(), fileName);
        File.WriteAllBytes(tempPath, new byte[] { 0x00, 0x00 }); // Arquivo dummy
        return tempPath;
    }

    public async Task InitializeAsync() {
        await _mongoDbContainer.StartAsync();

        // Limpa coleção antes dos testes
        var collection = _database.GetCollection<VideoResult>("VideoResults");
        await collection.DeleteManyAsync(Builders<VideoResult>.Filter.Empty);
    }

    public async Task DisposeAsync() {
        // Cleanup
        if (File.Exists(_testVideoPath))
            File.Delete(_testVideoPath);

        // Remove frames temporários
        var tempFramesPath = Path.Combine(Path.GetTempPath(), "scanforge_frames");
        if (Directory.Exists(tempFramesPath))
            Directory.Delete(tempFramesPath, true);

        if (_serviceProvider is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();

        await _mongoDbContainer.DisposeAsync();
    }

    [Fact]
    public async Task ProcessVideoAsync_WithValidFile_ShouldUpdateMongoDB() {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IVideoProcessingService>();
        var repository = scope.ServiceProvider.GetRequiredService<IVideoRepository>();

        var videoMessage = new VideoMessage {
            VideoId = 999,
            FilePath = _testVideoPath
        };

        // Act
        var exception = await Record.ExceptionAsync(() =>
            service.ProcessVideoAsync(videoMessage));

        // Assert
        Assert.Null(exception); // Não deve lançar exceção fatal

        var video = await repository.GetVideoByIdAsync(999);
        Assert.NotNull(video);
        Assert.Equal("Erro", video.Status); // Esperado erro devido ao arquivo dummy (não é vídeo válido)
        Assert.NotNull(video.ErrorMessage);
        Assert.Contains("FFMpeg", video.ErrorMessage ?? ""); // Erro de processamento FFMpeg
    }

    [Fact]
    public async Task ProcessVideoAsync_WithNonExistentFile_ShouldHandleErrorGracefully() {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IVideoProcessingService>();
        var repository = scope.ServiceProvider.GetRequiredService<IVideoRepository>();

        var videoMessage = new VideoMessage {
            VideoId = 1000,
            FilePath = "/app/uploads/nonexistent.mp4"
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.ProcessVideoAsync(videoMessage));

        Assert.Contains("não encontrado", exception.Message);

        // Verifica se status de erro foi atualizado
        var video = await repository.GetVideoByIdAsync(1000);
        Assert.NotNull(video);
        Assert.Equal("Erro", video.Status);
        Assert.Contains("não encontrado", video.ErrorMessage ?? "");
    }

    [Fact]
    public async Task ProcessVideoAsync_MultipleVideos_ShouldProcessIndependently() {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IVideoProcessingService>();
        var repository = scope.ServiceProvider.GetRequiredService<IVideoRepository>();

        // Correção: Usar método CreateTempFile agora disponível na classe
        var video1 = new VideoMessage { VideoId = 1001, FilePath = CreateTempFile("video1.mp4") };
        var video2 = new VideoMessage { VideoId = 1002, FilePath = CreateTempFile("video2.mp4") };

        // Act
        var exception1 = await Record.ExceptionAsync(() => service.ProcessVideoAsync(video1));
        var exception2 = await Record.ExceptionAsync(() => service.ProcessVideoAsync(video2));

        // Assert
        Assert.Null(exception1);
        Assert.Null(exception2);

        var result1 = await repository.GetVideoByIdAsync(1001);
        var result2 = await repository.GetVideoByIdAsync(1002);

        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal("Erro", result1.Status);
        Assert.Equal("Erro", result2.Status);
        Assert.NotEqual(result1.LastUpdated, result2.LastUpdated); // Processados em momentos diferentes

        // Cleanup dos arquivos temporários criados
        File.Delete(video1.FilePath);
        File.Delete(video2.FilePath);
    }

    [Fact]
    public async Task ProcessVideoAsync_TempFramesCleanup_ShouldRemoveTemporaryFiles() {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IVideoProcessingService>();
        var repository = scope.ServiceProvider.GetRequiredService<IVideoRepository>();

        var tempFramesPath = Path.Combine(Path.GetTempPath(), "scanforge_frames");
        var videoMessage = new VideoMessage {
            VideoId = 1003,
            FilePath = CreateTempFile("cleanup_test.mp4")
        };

        // Cria diretório de frames temporários para teste
        if (!Directory.Exists(tempFramesPath))
            Directory.CreateDirectory(tempFramesPath);

        // Cria alguns arquivos dummy de frame
        for (int i = 1; i <= 3; i++) {
            File.WriteAllBytes(Path.Combine(tempFramesPath, $"frame_{i:D4}.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // PNG header
        }

        // Act
        var exception = await Record.ExceptionAsync(() => service.ProcessVideoAsync(videoMessage));

        // Assert
        Assert.Null(exception);

        // Verifica se frames foram removidos (ou se o diretório foi recriado limpo)
        var framesAfter = Directory.GetFiles(tempFramesPath, "frame_*.png");
        Assert.Empty(framesAfter); // Deve estar vazio após cleanup

        // Cleanup
        File.Delete(videoMessage.FilePath);
        if (Directory.Exists(tempFramesPath))
            Directory.Delete(tempFramesPath, true);
    }
}