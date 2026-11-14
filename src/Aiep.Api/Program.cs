using Aiep.Api.Authentication;
using Aiep.Api.Endpoints;
using Aiep.Api.Http;
using Aiep.Infrastructure;
using Aiep.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemCodes.Apply);
builder.Services.AddInfrastructure();
builder.Services.AddAiepAuthentication();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<CredentialsInUrlMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapServiceHealthChecks();

// Versioned API surface (ADR-0015). Every endpoint requires authentication (fallback policy).
var v1 = app.MapGroup("/v1");
v1.MapMeEndpoints();
v1.MapOrganizationEndpoints();

app.Run();

// Exposes the entry point to WebApplicationFactory in integration tests.
public partial class Program
{
}
