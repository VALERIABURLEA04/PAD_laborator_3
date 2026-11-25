using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;

namespace BookCatalog.Api.Models;

[XmlRoot("Book")]
public sealed class BookRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;

    [Required, StringLength(120, MinimumLength = 1)]
    public string Author { get; set; } = string.Empty;

    [Range(1, 3000)]
    public int PublishedYear { get; set; }

    [StringLength(2000)]
    public string? Description { get; set; }

    [XmlArrayItem("Tag")]
    public List<string> Tags { get; set; } = [];
}
