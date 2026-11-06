using Microsoft.EntityFrameworkCore;
using Planner.Components;
using Planner.Data;
using Planner.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JiraOptions>(builder.Configuration.GetSection("Jira"));
builder.Services.Configure<PlannerOptions>(builder.Configuration.GetSection("Planner"));

builder.Services.AddDbContextFactory<PlannerDb>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Db")));

builder.Services.AddHttpClient<JiraClient>();
builder.Services.AddSingleton<AppState>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<PlanningService>();
builder.Services.AddScoped<BusinessImport>();
builder.Services.AddSingleton<InboxWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<InboxWatcher>());

builder.Services.AddRazorComponents().AddInteractiveServerComponents(o =>
{
    // держим состояние сессии дольше, чтобы короткий обрыв (сон ноутбука, сеть) не сбрасывал страницу
    o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(15);
    o.DisconnectedCircuitMaxRetained = 50;
});

var app = builder.Build();

await Seeder.InitAsync(app.Services);

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
