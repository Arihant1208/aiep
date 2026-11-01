using Aiep.Infrastructure;
using Aiep.ServiceDefaults;
using Aiep.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddInfrastructure();
builder.Services.AddHostedService<RunProcessor>();

builder.Build().Run();
