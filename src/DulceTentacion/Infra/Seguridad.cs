using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;

namespace DulceTentacion.Infra;

public static class Roles
{
    public const string Admin = "administrador";
    public const string Cajero = "cajero";
    public const string Mesero = "mesero";
    public static readonly string[] Todos = [Admin, Cajero, Mesero];
}

public static class Seguridad
{
    public static int UsuarioId(this ClaimsPrincipal u) => int.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static string Rol(this ClaimsPrincipal u) => u.FindFirstValue(ClaimTypes.Role)!;
    public static string NombreUsuario(this ClaimsPrincipal u) => u.FindFirstValue(ClaimTypes.Name) ?? "";
    public static bool EsAdmin(this ClaimsPrincipal u) => u.Rol() == Roles.Admin;
    /// <summary>Cobra la cuenta y cierra caja: el administrador y el cajero.</summary>
    public static bool PuedeCobrar(this ClaimsPrincipal u) => u.Rol() is Roles.Admin or Roles.Cajero;

    /// <summary>Limita un endpoint o un grupo a ciertos roles.</summary>
    public static TBuilder Solo<TBuilder>(this TBuilder b, params string[] roles) where TBuilder : IEndpointConventionBuilder =>
        b.RequireAuthorization(new AuthorizeAttribute { Roles = string.Join(",", roles) });

    public static string HashPin(string pin) => BCrypt.Net.BCrypt.HashPassword(pin, 10);

    /// <summary>Deja constancia de quién hizo algo importante (anulaciones, cambios de precio, cobros anulados…).</summary>
    public static Task Auditar(this SqliteConnection cn, int? usuarioId, string accion, string? detalle = null, SqliteTransaction? tx = null) =>
        cn.ExecuteAsync("INSERT INTO auditoria (usuario_id, accion, detalle) VALUES (@usuarioId, @accion, @detalle)",
            new { usuarioId, accion, detalle }, tx);
}
