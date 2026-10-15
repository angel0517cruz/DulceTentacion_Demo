using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DulceTentacion.Infra;

/// <summary>Error de regla de negocio: el mensaje se le muestra tal cual a la persona.</summary>
public sealed class NegocioException(string mensaje, int estado = 400, string? codigo = null) : Exception(mensaje)
{
    public int Estado { get; } = estado;
    public string? Codigo { get; } = codigo;
}

public static class Errores
{
    public static void UseManejoErrores(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (Exception ex) when (!ctx.Response.HasStarted)
            {
                var (estado, mensaje, codigo) = Traducir(ex, app.Logger);
                ctx.Response.StatusCode = estado;
                await ctx.Response.WriteAsJsonAsync(new { error = mensaje, codigo });
            }
        });
    }

    private static (int, string, string?) Traducir(Exception ex, ILogger log)
    {
        switch (ex)
        {
            case NegocioException n:
                return (n.Estado, n.Message, n.Codigo);

            case SqliteException { SqliteErrorCode: 19 } sql:   // restricción: único, check o llave foránea
                if (sql.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)) return (409, "Ese dato ya existe.", null);
                if (sql.Message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)) return (409, "No se puede, porque tiene registros asociados.", null);
                return (400, "El dato no es válido.", null);

            case SqliteException { SqliteErrorCode: 5 or 6 }:    // base ocupada
                return (503, "El sistema estaba ocupado. Intenta de nuevo.", null);

            case BadHttpRequestException or JsonException:
                return (400, "Los datos enviados no son válidos.", null);

            default:
                log.LogError(ex, "Error inesperado");
                return (500, "Ocurrió un error inesperado. Intenta de nuevo.", null);
        }
    }
}
