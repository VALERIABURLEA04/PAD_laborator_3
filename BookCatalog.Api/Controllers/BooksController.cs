using BookCatalog.Api.Models;
using BookCatalog.Api.Services;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BookCatalog.Api.Controllers;

[ApiController]
[Route("api/books")]
public sealed class BooksController(
    IMongoCollection<Book> books,
    BookCache cache,
    BookSyncOptions syncOptions,
    SyncVersionService versions) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<Book>>> GetAll(CancellationToken cancellationToken)
    {
        var result = await books.Find(x => !x.IsDeleted)
            .ToListAsync(cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Book>> GetById(string id, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out _)) return BadRequest("Invalid book id.");

        var cachedBook = await cache.GetAsync(id, cancellationToken);
        if (cachedBook is not null) return Ok(cachedBook);

        var book = await books.Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(cancellationToken);
        if (book is null) return NotFound();

        await cache.SetAsync(book, cancellationToken);
        return Ok(book);
    }

    [HttpPost]
    public async Task<ActionResult<Book>> Create(BookRequest request, CancellationToken cancellationToken)
    {
        var book = ToBook(request);
        if (syncOptions.Enabled)
            book.SyncVersion = await versions.NextAsync(cancellationToken);
        await books.InsertOneAsync(book, cancellationToken: cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = book.Id }, book);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<Book>> Replace(string id, BookRequest request, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out _)) return BadRequest("Invalid book id.");

        var book = ToBook(request);
        book.Id = id;
        if (syncOptions.Enabled)
            book.SyncVersion = await versions.NextAsync(cancellationToken);
        var result = await books.ReplaceOneAsync(x => x.Id == id && !x.IsDeleted, book,
            cancellationToken: cancellationToken);
        if (result.MatchedCount == 0) return NotFound();

        await cache.RemoveAsync(id, cancellationToken);
        return Ok(book);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out _)) return BadRequest("Invalid book id.");

        if (syncOptions.Enabled)
        {
            var update = Builders<Book>.Update
                .Set(x => x.IsDeleted, true)
                .Set(x => x.SyncVersion, await versions.NextAsync(cancellationToken))
                .Set(x => x.SyncOrigin, syncOptions.InstanceId);
            var result = await books.UpdateOneAsync(
                x => x.Id == id && !x.IsDeleted, update, cancellationToken: cancellationToken);
            if (result.MatchedCount == 0) return NotFound();
        }
        else
        {
            var result = await books.DeleteOneAsync(x => x.Id == id, cancellationToken);
            if (result.DeletedCount == 0) return NotFound();
        }

        await cache.RemoveAsync(id, cancellationToken);
        return NoContent();
    }

    private Book ToBook(BookRequest request) => new()
    {
        Title = request.Title.Trim(),
        Author = request.Author.Trim(),
        PublishedYear = request.PublishedYear,
        Description = request.Description?.Trim(),
        Tags = request.Tags,
        SyncOrigin = syncOptions.Enabled ? syncOptions.InstanceId : string.Empty
    };
}
