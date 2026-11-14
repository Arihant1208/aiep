using Aiep.Application.Execution;
using Aiep.Infrastructure;
using Aiep.ServiceDefaults;
using Aiep.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddInfrastructure();
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.SectionName));
builder.Services.AddSingleton<RunExecutor>();
builder.Services.AddHostedService<RunWorker>();
builder.Services.AddHostedService<ReconcilerService>();

builder.Build().Run();
