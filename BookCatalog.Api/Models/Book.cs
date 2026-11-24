using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;

namespace BookCatalog.Api.Models;

public sealed class Book
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    public required string Title { get; set; }
    public required string Author { get; set; }
    public int PublishedYear { get; set; }
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = [];

    [JsonIgnore]
    public long SyncVersion { get; set; }

    [JsonIgnore]
    public string SyncOrigin { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsDeleted { get; set; }
}
