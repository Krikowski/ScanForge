using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Collections.Generic;

namespace ScanForge.Models {
    public class VideoResult {
        [BsonId]
        [BsonRepresentation(BsonType.Int32)]
        public int VideoId { get; set; }

        public string Status { get; set; } = "Na Fila";

        public List<QRCodeResult> QRCodes { get; set; } = new List<QRCodeResult>();
    }
}