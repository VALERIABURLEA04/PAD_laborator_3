using BookCatalog.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace BookCatalog.Api.Controllers;

public sealed record FaultUpdate(bool Enabled);

[ApiController]
[Route("api/fault")]
[Produces("application/json")]
public sealed class FaultController(FaultState fault) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { enabled = fault.Enabled });

    [HttpPut]
    public IActionResult Put(FaultUpdate update)
    {
        fault.Enabled = update.Enabled;
        return Ok(new { enabled = fault.Enabled });
    }
}
