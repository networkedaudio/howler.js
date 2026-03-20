using MudBlazor.Services;
using RTPTransmitter.Components;
using RTPTransmitter.Hubs;
using RTPTransmitter.Services;

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

app.UseAntiforgery();

app.UseStaticFiles();
app.MapStaticAssets();

// Map the SignalR audio streaming hub
app.MapHub<AudioStreamHub>("/audiohub");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
