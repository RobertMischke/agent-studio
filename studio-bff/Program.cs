using System.Net.Http.Headers;
using AgentStudio.TaskServer.Contracts;

var builder = WebApplication.CreateBuilder(args);
var taskServerUrl = builder.Configuration["TaskServer:BaseUrl"]
    ?? throw new InvalidOperationException("TaskServer:BaseUrl is required.");
builder.Services.AddHttpClient("task-server", client =>
{
    client.BaseAddress = new Uri(taskServerUrl);
    client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
    client.DefaultRequestHeaders.Add(TaskServerProtocol.ClientVersionHeaderName,
        typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0");
});

var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok(new { status = "live", role = "studio-bff" }));
app.Map("/api/v1/{**path}", async context =>
{
    var client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("task-server");
    var target = context.Request.Path + context.Request.QueryString;
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
    if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        request.Content = new StreamContent(context.Request.Body);
        if (MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType))
            request.Content.Headers.ContentType = mediaType;
    }
    foreach (var header in new[] { "X-Actor-Id", "X-Client-Id", "Idempotency-Key", "If-Match" })
        if (context.Request.Headers.TryGetValue(header, out var value))
            request.Headers.TryAddWithoutValidation(header, value.ToArray());

    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    context.Response.StatusCode = (int)response.StatusCode;
    if (response.Content.Headers.ContentType is not null)
        context.Response.ContentType = response.Content.Headers.ContentType.ToString();
    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
});

await app.RunAsync();

public partial class Program;
