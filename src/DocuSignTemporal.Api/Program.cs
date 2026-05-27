using DocuSignTemporal.Worker.Activities;
using DocuSignTemporal.Worker.Services;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// ── Services ─────────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opts =>
{
    opts.SwaggerDoc("v1", new() { Title = "DocuSign Temporal API", Version = "v1" });
});

builder.Services.Configure<DocuSignOptions>(builder.Configuration.GetSection("DocuSign"));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));

// Temporal client (shared between API and Worker if co-hosted, or standalone for API-only)
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = builder.Configuration["Temporal:Host"] ?? "localhost:7233";
    opts.Namespace = builder.Configuration["Temporal:Namespace"] ?? "default";
});

// ── App ───────────────────────────────────────────────────────────────────────
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.MapControllers();
app.Run();
