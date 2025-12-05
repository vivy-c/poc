using CallTranscription.Functions.Common;
using CallTranscription.Functions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        config.AddJsonFile("local.settings.json", optional: true, reloadOnChange: true);
        config.AddEnvironmentVariables();
    })
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddLogging();
        services.AddSingleton<ResponseFactory>();
        services.AddSingleton<DemoUserStore>();
        services.AddSingleton<CallSessionStore>();
        services.AddSingleton<TranscriptStore>();
        services.AddSingleton<CallSummaryStore>();
        services.AddSingleton<AcsIdentityService>();
        services.AddSingleton<AcsCallService>();
        services.AddSingleton<AcsTranscriptionService>();
        services.Configure<AcsOptions>(context.Configuration.GetSection("ACS"));
        services.AddSingleton<CallSummaryService>();
        services.Configure<OpenAiOptions>(context.Configuration.GetSection("OpenAI"));
        services.Configure<SpeechOptions>(context.Configuration.GetSection("Speech"));
        services.Configure<WebhookAuthOptions>(context.Configuration.GetSection("Webhook"));
    })
    .Build();

await host.RunAsync();
