using DocuSignTemporal.Core.Interfaces;
using DocuSignTemporal.Worker.Activities;
using DocuSignTemporal.Worker.Services;
using DocuSignTemporal.Worker.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        // ── Options ──────────────────────────────────────────────────────────
        services.Configure<DocuSignOptions>(config.GetSection("DocuSign"));
        services.Configure<EmailOptions>(config.GetSection("Email"));

        // ── Application Services ─────────────────────────────────────────────
        services.AddSingleton<IPdfGeneratorService, PdfGeneratorService>();
        services.AddSingleton<IEmailNotificationService, EmailNotificationService>();

        // ── Temporal Worker ──────────────────────────────────────────────────
        services.AddTemporalClient(opts =>
        {
            opts.TargetHost = config["Temporal:Host"] ?? "localhost:7233";
            opts.Namespace = config["Temporal:Namespace"] ?? "default";
        });

        services.AddHostedTemporalWorker("docusign-signing-queue")
            .AddScopedActivities<DocuSignActivities>()
            .AddWorkflow<DocumentSigningWorkflow>()
            .AddWorkflow<BatchSigningWorkflow>();
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .Build();

await host.RunAsync();
