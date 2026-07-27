using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SignalTracker.Security;

public sealed class ProductionErrorResponseFilter : IResultFilter
{
    private static readonly object GenericErrorResponse = new
    {
        Status = 0,
        Message = "An unexpected server error occurred."
    };

    private readonly IWebHostEnvironment _environment;

    public ProductionErrorResponseFilter(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (_environment.IsDevelopment())
        {
            return;
        }

        switch (context.Result)
        {
            case ObjectResult objectResult when IsServerError(objectResult.StatusCode, context):
                objectResult.Value = GenericErrorResponse;
                objectResult.StatusCode ??= StatusCodes.Status500InternalServerError;
                break;

            case JsonResult jsonResult when IsServerError(jsonResult.StatusCode, context):
                jsonResult.Value = GenericErrorResponse;
                jsonResult.StatusCode ??= StatusCodes.Status500InternalServerError;
                break;
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    private static bool IsServerError(int? resultStatusCode, ResultExecutingContext context)
    {
        var statusCode = resultStatusCode ?? context.HttpContext.Response.StatusCode;
        return statusCode >= StatusCodes.Status500InternalServerError;
    }
}


