using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MaskFlow.Api.Tests;

public class ApiExceptionHandlerTests
{
    sealed class Feature(Exception error) : IExceptionHandlerFeature
    {
        public Exception Error { get; } = error;
    }

    static async Task<(int Status, string Detail)> Handle(Exception exception)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        context.Features.Set<IExceptionHandlerFeature>(new Feature(exception));
        await ApiExceptionHandler.HandleAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, document.RootElement.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task BusinessErrorsKeepStatusAndMessage()
    {
        var (status, detail) = await Handle(new BadHttpRequestException("Storage quota exceeded.", 403));
        Assert.Equal(403, status);
        Assert.Equal("Storage quota exceeded.", detail);
    }

    [Fact]
    public async Task UnexpectedErrorsHideInternals()
    {
        var (status, detail) = await Handle(new InvalidOperationException("connection string leaked here"));
        Assert.Equal(500, status);
        Assert.DoesNotContain("connection", detail);
    }
}
