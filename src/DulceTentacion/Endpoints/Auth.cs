using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using DulceTentacion.Infra;

namespace DulceTentacion.Endpoints;

public static class AuthEndpoints
{
    private record EntrarDto(int? UsuarioId, string? Pin);
    private record CambiarPinDto(string? Actual, string? Nuevo);
    private sealed class FilaUsuario
    {
        public long Id { get; set; }
        public string Nombre { get; set; } = "";
        public string Rol { get; set; } = "";
        public string PinHash { get; set; } = "";
        public long Activo { get; set; }
        public long IntentosFallidos { get; set; }
        public string? BloqueadoHasta { get; set; }
    }

    private const int MaxIntentos = 5;
    private const int MinutosBloqueo = 5;

    private static object Perfil(long id, string nombre, string rol) => new
    {
        id, nombre, rol,
        esAdmin = rol == Roles.Admin,
        puedeCobrar = rol is Roles.Admin or Roles.Cajero,
    };

    public static void MapAuth(this WebApplication app)
    {
        var g = app.MapGroup("/api");

        // Las meseras escogen su nombre en una pantalla con botones grandes (sin escribir usuario).
        g.MapGet("/personal", async (Db db) =>
        {
            await using var cn = await db.Abrir();
            return Results.Ok(await cn.QueryAsync("SELECT id, nombre, rol FROM usuario WHERE activo = 1 ORDER BY orden, nombre"));
        });

        g.MapPost("/auth/entrar", async (EntrarDto dto, Db db, HttpContext ctx) =>
        {
            if (dto.UsuarioId is null || string.IsNullOrWhiteSpace(dto.Pin)) throw new NegocioException("Elige tu nombre y escribe tu PIN.");
            await using var cn = await db.Abrir();
            var u = await cn.QuerySingleOrDefaultAsync<FilaUsuario>(
                @"SELECT id AS Id, nombre AS Nombre, rol AS Rol, pin_hash AS PinHash, activo AS Activo,
                         intentos_fallidos AS IntentosFallidos, bloqueado_hasta AS BloqueadoHasta
                  FROM usuario WHERE id = @id", new { id = dto.UsuarioId });
            if (u is null || u.Activo != 1) throw new NegocioException("Ese usuario no está disponible.", 403);

            if (u.BloqueadoHasta is { } hasta && DateTime.TryParse(hasta, out var fin) && fin > DateTime.Now)
                throw new NegocioException($"Demasiados intentos. Espera {Math.Max(1, (int)Math.Ceiling((fin - DateTime.Now).TotalMinutes))} minuto(s).", 429);

            if (!BCrypt.Net.BCrypt.Verify(dto.Pin, u.PinHash))
            {
                var intentos = u.IntentosFallidos + 1;
                var bloquear = intentos >= MaxIntentos;
                await cn.ExecuteAsync(
                    "UPDATE usuario SET intentos_fallidos = @n, bloqueado_hasta = @b WHERE id = @id",
                    new { n = bloquear ? 0 : intentos, b = bloquear ? DateTime.Now.AddMinutes(MinutosBloqueo).ToString("yyyy-MM-dd HH:mm:ss") : null, id = u.Id });
                throw new NegocioException("PIN incorrecto.", 401);
            }

            await cn.ExecuteAsync("UPDATE usuario SET intentos_fallidos = 0, bloqueado_hasta = NULL WHERE id = @id", new { id = u.Id });
            var identidad = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
                new Claim(ClaimTypes.Name, u.Nombre),
                new Claim(ClaimTypes.Role, u.Rol),
            ], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(new ClaimsPrincipal(identidad), new AuthenticationProperties { IsPersistent = true });
            return Results.Ok(Perfil(u.Id, u.Nombre, u.Rol));
        });

        g.MapPost("/auth/salir", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync();
            return Results.Ok(new { ok = true });
        });

        g.MapGet("/auth/yo", (HttpContext ctx) =>
            Results.Ok(Perfil(ctx.User.UsuarioId(), ctx.User.NombreUsuario(), ctx.User.Rol()))).RequireAuthorization();

        // Administración cambia su PIN al instante. Meseras y cajeros dejan una solicitud que un administrador debe aprobar.
        g.MapPost("/auth/cambiar-pin", async (CambiarPinDto dto, Db db, HttpContext ctx, Eventos ev) =>
        {
            var nuevo = V.Pin(dto.Nuevo);
            await using var cn = await db.Abrir();
            var hash = await cn.ExecuteScalarAsync<string>("SELECT pin_hash FROM usuario WHERE id = @id", new { id = ctx.User.UsuarioId() });
            if (string.IsNullOrEmpty(dto.Actual) || !BCrypt.Net.BCrypt.Verify(dto.Actual, hash))
                throw new NegocioException("Tu PIN actual no es correcto.");

            if (ctx.User.EsAdmin())
            {
                await cn.ExecuteAsync("UPDATE usuario SET pin_hash = @h WHERE id = @id", new { id = ctx.User.UsuarioId(), h = Seguridad.HashPin(nuevo) });
                await cn.Auditar(ctx.User.UsuarioId(), "pin_cambiado");
                return Results.Ok(new { pendiente = false, mensaje = "Tu PIN se cambió." });
            }

            await cn.ExecuteAsync("DELETE FROM solicitud_pin WHERE usuario_id = @id AND estado = 'pendiente'", new { id = ctx.User.UsuarioId() });
            await cn.ExecuteAsync("INSERT INTO solicitud_pin (usuario_id, pin_hash) VALUES (@id, @h)",
                new { id = ctx.User.UsuarioId(), h = Seguridad.HashPin(nuevo) });
            await cn.Auditar(ctx.User.UsuarioId(), "pin_solicitado");
            ev.Emitir("solicitud_pin");
            return Results.Ok(new { pendiente = true, mensaje = "Listo: enviamos la solicitud al administrador. Tu PIN cambia cuando la apruebe; mientras tanto sigue valiendo el actual." });
        }).RequireAuthorization();
    }
}
