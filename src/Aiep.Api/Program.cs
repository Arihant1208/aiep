using Aiep.Api.Http;
using Aiep.Infrastructure;
using Aiep.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapServiceHealthChecks();

// Versioned API surface (ADR-0015). Endpoint groups are added per module from Phase 1 onward.

app.Run();

// Exposes the entry point to WebApplicationFactory in integration tests.
public partial class Program
{
}
