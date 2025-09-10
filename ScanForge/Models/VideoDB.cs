namespace ScanForge.Models {
    public class VideoDB {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int Duration { get; set; }
        public string? FilePath { get; set; }
        public string? Status { get; set; }
        public DateTime? CreatedAt { get; set; }
        public List<QRCodeResult> QRCodes { get; set; } = new List<QRCodeResult>();
    }

    public class QRCodeResult {
        public int Id { get; set; }
        public int VideoId { get; set; }
        public string? Content { get; set; }
        public int Timestamp { get; set; }
        public VideoDB? Video { get; set; }
    }
}