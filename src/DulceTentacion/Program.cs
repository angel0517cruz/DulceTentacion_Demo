using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using DulceTentacion.Endpoints;
using DulceTentacion.Infra;

var builder = WebApplication.CreateBuilder(args);

// Render entrega el puerto en la variable PORT.
var puertoRender = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(puertoRender)) builder.WebHost.UseUrls($"http://0.0.0.0:{puertoRender}");

// Puerto: 8080 por defecto, o el que se indique con --urls / ASPNETCORE_URLS / appsettings.
if (string.IsNullOrEmpty(puertoRender) && string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]))
    builder.WebHost.UseUrls("http://0.0.0.0:8080");

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<Eventos>();
builder.Services.AddSingleton<Respaldo>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Respaldo>());

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "dt_sesion";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        // En una API no se redirige a una página de login: se responde 401/403 y el front end decide.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        // El rol y el estado se revisan en cada petición: si se desactiva a alguien, deja de entrar de inmediato.
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<Db>();
            try
            {
                await using var cn = await db.Abrir();
                var u = await cn.QuerySingleOrDefaultAsync("SELECT rol, nombre, activo FROM usuario WHERE id = @id",
                    new { id = ctx.Principal!.UsuarioId() });
                if (u is null || u.activo != 1 || (string)u.rol != ctx.Principal!.Rol())
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync();
                }
            }
            catch { ctx.RejectPrincipal(); }
        };
    });
builder.Services.AddAuthorization();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);

var app = builder.Build();

app.UseManejoErrores();

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "same-origin";
    h["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'";
    if (ctx.Request.Path.StartsWithSegments("/api")) h["Cache-Control"] = "no-store";
    await next();
});

await app.Services.GetRequiredService<Db>().Preparar();
var demo = app.Configuration.GetValue<bool>("Demo:Activo");
await Semilla.Cargar(app.Services.GetRequiredService<Db>(), app.Logger, demo);

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // El navegador revisa siempre si hay versión nueva: al actualizar el sistema nadie ve pantallas viejas.
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/salud", () => Results.Ok(new { ok = true, hora = Db.Ahora() }));
// La pantalla de acceso muestra el aviso de demostración y el PIN de prueba.
app.MapGet("/api/demo", () => Results.Ok(new { activo = demo, pin = demo ? "1234" : null }));

// Eventos en vivo (SSE): cada celular se entera cuando cambia una mesa, una cuenta o el menú.
app.MapGet("/api/eventos", async (HttpContext ctx, Eventos eventos) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    var (id, lector) = eventos.Suscribir();
    try
    {
        await ctx.Response.WriteAsync("retry: 2000\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        while (!ctx.RequestAborted.IsCancellationRequested)
        {
            using var espera = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            espera.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var mensaje = await lector.ReadAsync(espera.Token);
                await ctx.Response.WriteAsync($"data: {mensaje}\n\n", ctx.RequestAborted);
            }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                await ctx.Response.WriteAsync(": latido\n\n", ctx.RequestAborted);   // mantiene viva la conexión
            }
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    catch (System.Threading.Channels.ChannelClosedException) { }
    finally { eventos.Quitar(id); }
}).RequireAuthorization();

app.MapAuth();
app.MapMenu();
app.MapCuentas();
app.MapCobro();
app.MapCaja();
app.MapAdmin();
app.MapEstadisticas();
app.MapExcel();

app.MapFallback("/api/{**ruta}", () => Results.NotFound(new { error = "Ese servicio no existe." }));

app.Run();
