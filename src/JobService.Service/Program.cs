using Azure.Messaging.ServiceBus;
using JobService.Components;
using JobService.Service.Components;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using NSwag;
using Serilog;
using Serilog.Events;
using System;
using System.Reflection;
using System.Threading.Tasks;

const string sb = "Endpoint=sb://...";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("MassTransit", LogEventLevel.Debug)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddControllers();
builder.Services.AddOpenApiDocument(cfg => cfg.PostProcess = d =>
{
    d.Info.Title = "Job Consumer Sample";
    d.Info.Contact = new OpenApiContact
    {
        Name = "Job Consumer Sample using MassTransit",
        Email = "support@masstransit.io"
    };
});


builder.Services.AddSingleton(x => new ServiceBusClient(sb));
builder.Services.AddMassTransit(x =>
{
    x.AddDelayedMessageScheduler();

    x.AddConsumer<ConvertVideoJobConsumer, ConvertVideoJobConsumerDefinition>()
        .Endpoint(e => e.Name = "convert-job-queue");

    x.AddConsumer<TrackVideoConvertedConsumer>();
    x.SetJobConsumerOptions();

    const string sagaTableName = "MTJobSaga";
    var tableAccount = CloudStorageAccount.Parse("UseDevelopmentStorage=true;");
    var tableClient = tableAccount.CreateCloudTableClient();
    tableClient.GetTableReference(sagaTableName).CreateIfNotExists();

    x.AddJobSagaStateMachines(options => options.FinalizeCompleted = false)
        .AzureTableRepository(c => c.ConnectionFactory(() => tableClient.GetTableReference(sagaTableName)));

    x.SetKebabCaseEndpointNameFormatter();

    x.AddServiceBusMessageScheduler();
    x.UsingAzureServiceBus((context, cfg) =>
    {
        cfg.Host(sb);

        cfg.UseRawJsonSerializer();
        cfg.UseServiceBusMessageScheduler();
        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddOptions<MassTransitHostOptions>()
    .Configure(options =>
    {
        options.WaitUntilStarted = true;
        options.StartTimeout = TimeSpan.FromMinutes(1);
        options.StopTimeout = TimeSpan.FromMinutes(1);
    });

builder.Services.AddOptions<HostOptions>()
    .Configure(options => options.ShutdownTimeout = TimeSpan.FromMinutes(1));

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();

app.UseOpenApi();
app.UseSwaggerUi3();

app.UseRouting();
app.UseAuthorization();

static Task HealthCheckResponseWriter(HttpContext context, HealthReport result)
{
    context.Response.ContentType = "application/json";

    return context.Response.WriteAsync(result.ToJsonString());
}

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthCheckResponseWriter
});

app.MapHealthChecks("/health/live", new HealthCheckOptions { ResponseWriter = HealthCheckResponseWriter });

app.MapControllers();

await app.RunAsync();