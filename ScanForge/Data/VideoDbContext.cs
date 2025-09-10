using Microsoft.EntityFrameworkCore;
using ScanForge.Models;

namespace ScanForge.Data {
    public class VideoDbContext : DbContext {
        public VideoDbContext(DbContextOptions<VideoDbContext> options) : base(options) {
        }

        public DbSet<VideoDB> Videos { get; set; }
        public DbSet<QRCodeResult> QRCodes { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder) {
            modelBuilder.Entity<VideoDB>().ToTable("Videos", schema: "public"); // Ajustado para "Videos"
            modelBuilder.Entity<QRCodeResult>().ToTable("QRCodes", schema: "public");
            base.OnModelCreating(modelBuilder);
        }
    }
}