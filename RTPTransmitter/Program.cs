using MudBlazor.Services;
using RTPTransmitter.Api;
using RTPTransmitter.Components;
using RTPTransmitter.Hubs;
using RTPTransmitter.Services;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

// Configure SignalR with larger message size for audio chunks
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 512 * 1024; // 512 KB
});

// API controllers + Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "RTP Transmitter API",
        Version = "v1",
        Description = "API for managing AES67 stream discovery and recording."
    });

    // API key auth in Swagger UI
    c.AddSecurityDefinition(ApiKeyAuthHandler.SchemeName, new OpenApiSecurityScheme
    {
        Name = ApiKeyAuthHandler.HeaderName,
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Description = "API key passed via the X-Api-Key header."
    });

    c.AddSecurityRequirement((document) => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference(ApiKeyAuthHandler.SchemeName, document)] = new List<string>()
    });
});

// API key authentication
builder.Services.AddAuthentication(ApiKeyAuthHandler.SchemeName)
    .AddScheme<ApiKeyAuthOptions, ApiKeyAuthHandler>(ApiKeyAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

// Bind RTP listener configuration from appsettings.json
builder.Services.Configure<RtpListenerOptions>(
    builder.Configuration.GetSection(RtpListenerOptions.Section));

// Register the background RTP listener service (for the static "default" stream)
builder.Services.AddHostedService<RtpListenerService>();

// SAP/SDP discovery services
builder.Services.Configure<SapListenerOptions>(
    builder.Configuration.GetSection(SapListenerOptions.Section));
builder.Services.AddSingleton<SapStreamRegistry>();
builder.Services.AddHostedService<SapDiscoveryService>();

// Network interface service (runtime NIC selection for multicast binding)
builder.Services.AddSingleton<NetworkInterfaceService>();

// Dynamic RTP stream manager (starts/stops listeners for SAP-discovered streams)
builder.Services.AddSingleton<RtpStreamManager>();

// Channel recording service (per-channel capture with silence detection)
builder.Services.Configure<RecordingOptions>(
    builder.Configuration.GetSection(RecordingOptions.Section));
builder.Services.AddSingleton<ChannelRecordingService>();

// Soundcard capture (PvRecorder-based local recording device input)
builder.Services.AddSingleton<SoundcardCaptureService>();

// Ensure static web assets manifest is loaded in all environments (not just Development)
if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// Swagger UI (available in all environments)
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "RTP Transmitter API v1");
    c.RoutePrefix = "swagger";
});

app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

app.UseStaticFiles();
app.MapStaticAssets();

// Map API controllers
app.MapControllers();

// Map the SignalR audio streaming hub
app.MapHub<AudioStreamHub>("/audiohub");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
