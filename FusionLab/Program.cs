using FusionLab.Components;
using Fusion.Common.Contracts;
using Fusion.Core;
using Fusion.Core.Replay;
using FusionLab.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<ElementDiscoveryService>();
builder.Services.AddSingleton<AiBlueprintService>();
builder.Services.AddSingleton<CompoundService>();
builder = builder.AddCoreServices();

// --- Flow visualization / bus replay wiring ---
// FusionLab hosts its own in-process message bus (it does not run the full Fusion core).
// The Flow view subscribes to it for live traffic, and the ReplayService re-publishes
// recorded audit-log traffic onto it using REPLAY.* namespaced topics ONLY — replayed
// messages must never appear on original topics where they could trigger real reactions.
builder.Services.AddSingleton(Serilog.Log.Logger);
builder.Services.AddSingleton(provider =>
    new BusAuditLogger(provider.GetRequiredService<ILogger<BusAuditLogger>>())
    {
        // FusionLab is a viewer: it must not write audit files (it has no audit sink,
        // and auditing replayed traffic would pollute the historical record anyway).
        IsEnabled = false
    });
builder.Services.AddSingleton<IMessageBus, MessageBus>();
builder.Services.AddSingleton(provider => new ReplayService(provider.GetRequiredService<IMessageBus>()));
builder.Services.AddSingleton<FlowGraphService>();

                   
var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();