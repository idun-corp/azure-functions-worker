using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace AzureFunctions.Worker.Extensions.Tests.FunctionApp.Functions;

/// <summary>
/// Task-returning (void) HTTP functions: the worker middleware's NoContent fallback
/// produces their responses, which is where auth challenges used to be masked as 204.
/// </summary>
[Authorize]
public class VoidFunctions
{
    [Function($"{nameof(VoidFunctions)}-{nameof(AuthorizedVoid)}")]
    public Task AuthorizedVoid(
        [HttpTrigger("GET", Route = "void/authorized")] HttpRequest request)
    {
        return Task.CompletedTask;
    }

    [AllowAnonymous]
    [Function($"{nameof(VoidFunctions)}-{nameof(AnonymousVoid)}")]
    public Task AnonymousVoid(
        [HttpTrigger("GET", Route = "void/anonymous")] HttpRequest request)
    {
        return Task.CompletedTask;
    }

    [AllowAnonymous]
    [Function($"{nameof(VoidFunctions)}-{nameof(AnonymousResult)}")]
    public Task<IActionResult> AnonymousResult(
        [HttpTrigger("GET", Route = "result/anonymous")] HttpRequest request)
    {
        return Task.FromResult<IActionResult>(new OkObjectResult(new { Status = "ok" }));
    }
}
