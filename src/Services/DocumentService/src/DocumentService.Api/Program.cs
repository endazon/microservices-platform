using KnowledgePlatform.Shared.Infrastructure.Extensions;
using DocumentService.Api.Endpoints;
using Serilog;

const string ServiceName = "knowledge-platform.document-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
builder.Services.AddKnowledgePlatformHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=document_svc;Username=kp;Password=kp",
        tags: ["ready"])
    .AddRabbitMQ(
        rabbitConnectionString: builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672",
        tags: ["ready"]);
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

DocumentEndpoints.Map(app);

app.Run();

public partial class Program { }
