using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Backend.Middleware;

public class GlobalExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlerMiddleware> _logger;

    public GlobalExceptionHandlerMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlerMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception occurred");
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        // Get the error endpoint URL from environment variable
        var errorEndpointUrl = Environment.GetEnvironmentVariable("RUNTIME_ERROR_ENDPOINT_URL");
        
        // If endpoint is configured, send error details to it (fire and forget)
        if (!string.IsNullOrWhiteSpace(errorEndpointUrl))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await SendErrorToEndpointAsync(errorEndpointUrl, context, exception);
                }
                catch (Exception sendEx)
                {
                    _logger.LogError(sendEx, "Failed to send error to endpoint: {Endpoint}", errorEndpointUrl);
                }
            });
        }

        // Return error response to client
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

        var response = new
        {
            error = "An error occurred while processing your request",
            message = exception.Message
        };

        var json = JsonSerializer.Serialize(response);
        await context.Response.WriteAsync(json);
    }

    private async Task SendErrorToEndpointAsync(string endpointUrl, HttpContext context, Exception exception)
    {
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(5);

        // Extract board ID from request path or headers
        var boardId = ExtractBoardId(context);

        var errorPayload = new
        {
            boardId = boardId,
            timestamp = DateTime.UtcNow,
            file = exception.Source,
            line = GetLineNumber(exception),
            stackTrace = exception.StackTrace,
            message = exception.Message,
            exceptionType = exception.GetType().Name,
            requestPath = context.Request.Path.ToString(),
            requestMethod = context.Request.Method,
            userAgent = context.Request.Headers["User-Agent"].ToString(),
            innerException = exception.InnerException != null ? new
            {
                message = exception.InnerException.Message,
                type = exception.InnerException.GetType().Name,
                stackTrace = exception.InnerException.StackTrace
            } : null
        };

        var json = JsonSerializer.Serialize(errorPayload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(endpointUrl, content);
        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Successfully sent runtime error to endpoint: {Endpoint}", endpointUrl);
        }
    }

    private string? ExtractBoardId(HttpContext context)
    {
        // Try route data
        if (context.Request.RouteValues.TryGetValue("boardId", out var boardIdObj))
            return boardIdObj?.ToString();
        
        // Try query string
        if (context.Request.Query.TryGetValue("boardId", out var boardIdQuery))
            return boardIdQuery.ToString();
        
        // Try header
        if (context.Request.Headers.TryGetValue("X-Board-Id", out var boardIdHeader))
            return boardIdHeader.ToString();
        
        return null;
    }

    private int? GetLineNumber(Exception exception)
    {
        var stackTrace = exception.StackTrace;
        if (string.IsNullOrEmpty(stackTrace)) return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            stackTrace,
            ":line\\s+(\\d+)|:(\\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (match.Success)
        {
            var lineStr = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (int.TryParse(lineStr, out var line))
                return line;
        }

        return null;
    }
}
