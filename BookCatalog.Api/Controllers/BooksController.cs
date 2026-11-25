using BookCatalog.Api.Models;
using BookCatalog.Api.Services;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BookCatalog.Api.Controllers;

[ApiController]
[Route("api/books")]
public sealed class BooksController(ShardRouter router, BookCache cache) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<Book>>> GetAll(CancellationToken cancellationToken)
    {
        var perShard = await Task.WhenAll(router.Shards.Select(async shard =>
        {
            try
            {
                return (shard.Name, Books: await shard.Books.Find(FilterDefinition<Book>.Empty).ToListAsync(cancellationToken));
            }
            catch (Exception exception) when (ShardUnavailable.Matches(exception))
            {
                return (shard.Name, Books: (List<Book>?)null);
            }
        }));

        var unavailable = perShard.Where(r => r.Books is null).Select(r => r.Name).ToList();
        if (unavailable.Count == perShard.Length) throw new TimeoutException("No shard is reachable.");
        if (unavailable.Count > 0) Response.Headers["X-Shards-Unavailable"] = string.Join(",", unavailable);
        Response.Headers["X-Shard"] = string.Join(",", perShard.Where(r => r.Books is not null).Select(r => r.Name));

        return Ok(perShard.SelectMany(r => r.Books ?? new List<Book>()).OrderBy(b => b.Id, StringComparer.Ordinal).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Book>> GetById(string id, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out _)) return BadRequest("Invalid book id.");
        var shard = router.For(id);
        Response.Headers["X-Shard"] = shard.Name;

        var cachedBook = await cache.GetAsync(id, cancellationToken);
        if (cachedBook is not null) return Ok(cachedBook);

        var book = await shard.Books.Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken);
        if (book is null) return NotFound();

        await cache.SetAsync(book, cancellationToken);
        return Ok(book);
    }

    [HttpPost]
    public async Task<ActionResult<Book>> Create(BookRequest request, CancellationToken cancellationToken)
    {
        // A new book has no id yet, so if its shard is down we can pick another id that lands on a live shard.
        var unavailable = new HashSet<string>();
        while (true)
        {
            var book = ToBook(request, ShardRouter.NewId());
            var shard = router.For(book.Id);
            if (unavailable.Contains(shard.Name)) continue;

            Response.Headers["X-Shard"] = shard.Name;
            try
            {
                await shard.Books.InsertOneAsync(book, cancellationToken: cancellationToken);
                return CreatedAtAction(nameof(GetById), new { id = book.Id }, book);
            }
            catch (Exception exception) when (ShardUnavailable.Matches(exception) &&
                                              unavailable.Count < router.Shards.Count - 1)
            {
                unavailable.Add(shard.Name);
            }
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<Book>> Replace(string id, BookRequest request, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out _)) return BadRequest("Invalid book id.");
        var shard = router.For(id);
        Response.Headers["X-Shard"] = shard.Name;

        var book = ToBook(request, id);
        var result = await shard.Books.ReplaceOneAsync(x => x.Id == id, book, cancellationToken: cancellationToken);
        if (result.MatchedCount == 0) return NotFound();

        await cache.RemoveAsync(id, cancellationToken);
        return Ok(book);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out _)) return BadRequest("Invalid book id.");
        var shard = router.For(id);
        Response.Headers["X-Shard"] = shard.Name;

        var result = await shard.Books.DeleteOneAsync(x => x.Id == id, cancellationToken);
        if (result.DeletedCount == 0) return NotFound();

        await cache.RemoveAsync(id, cancellationToken);
        return NoContent();
    }

    private static Book ToBook(BookRequest request, string id) => new()
    {
        Id = id,
        Title = request.Title.Trim(),
        Author = request.Author.Trim(),
        PublishedYear = request.PublishedYear,
        Description = request.Description?.Trim(),
        Tags = request.Tags
    };
}
