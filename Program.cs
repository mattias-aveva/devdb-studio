using DevDbStudio.Components;
using DevDbStudio.Services;
using DevDbStudio.Services.Dbms;

var builder = WebApplication.CreateBuilder(args);

// Host filtering only applies while remote access is off; enabling remote access implies accepting other host names.
if (builder.Configuration.GetValue<bool>("Security:AllowRemoteAccess"))
    builder.Configuration["AllowedHosts"] = "*";

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<WslOptions>(builder.Configuration.GetSection("Wsl"));
builder.Services.AddScoped<IPrerequisiteCheckService, PrerequisiteCheckService>();
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.AddSingleton<IContainerEngine, ContainerEngine>();
builder.Services.AddSingleton<IDockerService, DockerService>();
builder.Services.AddSingleton<IWslDockerRunner, WslDockerRunner>();
builder.Services.AddSingleton<ISharedContainerNetwork, SharedContainerNetwork>();
builder.Services.AddSingleton<IInstanceRepository, JsonInstanceRepository>();
builder.Services.AddSingleton<IDbmsProvider, MssqlProvider>();
builder.Services.AddSingleton<IDbmsProvider, PostgresProvider>();
builder.Services.AddSingleton<IDbmsProvider, MariaDbProvider>();
builder.Services.AddSingleton<IDbmsProvider, MySqlProvider>();
builder.Services.AddSingleton<IDbmsProvider, MongoDbProvider>();
builder.Services.AddSingleton<IDbmsProvider, TimescaleDbProvider>();
builder.Services.AddSingleton<IDbmsProvider, RedisProvider>();
builder.Services.AddSingleton<IDbmsProvider, CouchDbProvider>();
builder.Services.AddSingleton<IInstanceManager, InstanceManager>();
builder.Services.AddSingleton<ITableDataService, TableDataService>();
builder.Services.AddSingleton<IDbInsightService, DbInsightService>();
builder.Services.AddHostedService<WslKeepAliveService>();

var app = builder.Build();

// The app can control Docker and has no authentication, so only loopback clients are served unless explicitly allowed.
if (!app.Configuration.GetValue<bool>("Security:AllowRemoteAccess"))
{
    app.Use(async (ctx, next) =>
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is not null && !System.Net.IPAddress.IsLoopback(ip))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("Remote access is disabled.");
            return;
        }
        // Block cross-site WebSocket/circuit hijacking from a page in the user's browser.
        if (ctx.Request.Headers.Origin is { Count: > 0 } origin
            && Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var o)
            && !string.Equals(o.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("Cross-origin requests are not allowed.");
            return;
        }
        await next();
    });
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
// HTTPS redirection/HSTS are intentionally omitted: the app is HTTP-only on loopback (no HTTPS port is configured).

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
