using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace BookCatalog.Api.Models;

[XmlRoot("Book")]
[BsonIgnoreExtraElements]
public sealed class Book
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public int PublishedYear { get; set; }
    public string? Description { get; set; }

    [XmlArrayItem("Tag")]
    public List<string> Tags { get; set; } = [];

    [JsonIgnore, XmlIgnore]
    public long SyncVersion { get; set; }

    [JsonIgnore, XmlIgnore]
    public string SyncOrigin { get; set; } = string.Empty;

    [JsonIgnore, XmlIgnore]
    public bool IsDeleted { get; set; }
}
