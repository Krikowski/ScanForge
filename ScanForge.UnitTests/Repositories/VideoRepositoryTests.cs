using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using MongoDB.Bson;
using MongoDB.Driver;
using ScanForge.Models;
using ScanForge.Repositories; // Usar do principal
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace ScanForge.Repositories;

//public interface IVideoRepository {
//    Task<VideoResult?> GetVideoByIdAsync(int id);
//    Task UpdateVideoAsync(VideoResult video);
//    Task SaveVideoAsync(VideoResult video);
//    Task EnsureIndexesAsync();
//    Task UpdateStatusAsync(int videoId, string status, string? errorMessage = null, int duration = 0);
//    Task AddQRCodesAsync(int videoId, List<QRCodeResult> qrs);
//}

public class VideoRepository : IVideoRepository {
    private readonly IMongoCollection<VideoResult> _videos;
    private readonly ILogger<VideoRepository> _logger;

    public VideoRepository(IMongoCollection<VideoResult> videos, ILogger<VideoRepository> logger) {
        _videos = videos ?? throw new ArgumentNullException(nameof(videos));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private async Task CreateIndexesAsync() {
        try {
            var pingResult = await _videos.Database.RunCommandAsync((Command<BsonDocument>)"{ping:1}");
            _logger.LogDebug("✅ Conectividade MongoDB confirmada");
            var statusIndex = Builders<VideoResult>.IndexKeys.Ascending(v => v.Status);
            await _videos.Indexes.CreateOneAsync(new CreateIndexModel<VideoResult>(statusIndex, new CreateIndexOptions { Name = "Status_Index" }));
            var statusCreatedIndex = Builders<VideoResult>.IndexKeys.Ascending(v => v.Status).Descending(v => v.CreatedAt);
            await _videos.Indexes.CreateOneAsync(new CreateIndexModel<VideoResult>(statusCreatedIndex, new CreateIndexOptions { Name = "Status_CreatedAt_Index" }));
            _logger.LogInformation("✅ Índices criados: Status_Index, Status_CreatedAt_Index");
        } catch (MongoCommandException ex) when (ex.Code == 85 || ex.Code == 86 || ex.Code == 11000) {
            _logger.LogDebug("Índices já existem - ignorando");
        } catch (Exception ex) {
            _logger.LogWarning(ex, "⚠️ Falha ao criar índices MongoDB (continuando sem índices otimizados)");
        }
    }

    public async Task EnsureIndexesAsync() {
        await CreateIndexesAsync();
    }

    public async Task<VideoResult?> GetVideoByIdAsync(int id) {
        return await _videos.Find(v => v.VideoId == id).FirstOrDefaultAsync();
    }

    public async Task UpdateVideoAsync(VideoResult video) {
        await _videos.ReplaceOneAsync(v => v.VideoId == video.VideoId, video);
    }

    public async Task SaveVideoAsync(VideoResult video) {
        await _videos.InsertOneAsync(video);
    }

    public async Task UpdateStatusAsync(int videoId, string status, string? errorMessage = null, int duration = 0) {
        var update = Builders<VideoResult>.Update
            .Set(v => v.Status, status)
            .Set(v => v.LastUpdated, DateTime.UtcNow);
        if (errorMessage != null) {
            update = update.Set(v => v.ErrorMessage, errorMessage);
        }
        if (duration > 0) {
            update = update.Set(v => v.Duration, duration);
        }
        await _videos.UpdateOneAsync(v => v.VideoId == videoId, update);
    }

    public async Task AddQRCodesAsync(int videoId, List<QRCodeResult> qrs) {
        var update = Builders<VideoResult>.Update
            .PushEach(v => v.QRCodes, qrs)
            .Set(v => v.LastUpdated, DateTime.UtcNow);
        await _videos.UpdateOneAsync(v => v.VideoId == videoId, update);
    }
}