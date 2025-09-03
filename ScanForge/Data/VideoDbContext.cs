using Microsoft.EntityFrameworkCore;
using ScanForge.Models;

namespace ScanForge.Data {
    public class VideoDbContext : DbContext {
        public VideoDbContext(DbContextOptions<VideoDbContext> options) : base(options) {
        }

        public DbSet<VideoDB> Videos { get; set; }
    }
}