using System.Text.Json;
using Dapper;
using DulceTentacion.Infra;

namespace DulceTentacion.Endpoints;

/// <summary>
/// Cierre de caja. Los números salen SOLO de lo que cuenta quien cierra y de los gastos registrados;
/// no se calculan a partir de los pedidos ni de las ventas.
///   ingreso del día = efectivo contado + otros reportados + gastos − base inicial
/// </summary>
public static class CajaEndpoints
{
    private record GastoDto(string? Concepto, long? Monto);
    private record CierreDto(long? BaseInicial, long? EfectivoContado, long? OtrosReportados, JsonElement? Desglose, string? Observaciones);

    private const string SqlCierres =
        @"SELECT c.id, c.fecha, c.usuario_id AS usuarioId, u.nombre AS usuario, c.base_inicial AS baseInicial,
                 c.efectivo_contado AS efectivoContado, c.otros_reportados AS otrosReportados, c.observaciones, c.creado_en AS creadoEn,
                 COALESCE((SELECT SUM(g.monto) FROM gasto g WHERE g.cierre_id = c.id), 0) AS gastos,
                 c.efectivo_contado + c.otros_reportados - c.base_inicial
                   + COALESCE((SELECT SUM(g.monto) FROM gasto g WHERE g.cierre_id = c.id), 0) AS ingreso
          FROM cierre_caja c JOIN usuario u ON u.id = c.usuario_id";

    public static void MapCaja(this WebApplication app)
    {
        var g = app.MapGroup("/api/caja").RequireAuthorization().Solo(Roles.Admin, Roles.Cajero);

        // Lo pendiente del día: gastos sin cierre y cierres ya hechos hoy.
        g.MapGet("/hoy", async (Db db) =>
        {
            await using var cn = await db.Abrir();
            var gastos = await cn.QueryAsync(
                @"SELECT g.id, g.concepto, g.monto, g.fecha, u.nombre AS usuario, g.creado_en AS creadoEn
                  FROM gasto g JOIN usuario u ON u.id = g.usuario_id WHERE g.cierre_id IS NULL ORDER BY g.id");
            var cierres = await cn.QueryAsync($"{SqlCierres} WHERE c.fecha = @hoy ORDER BY c.id DESC", new { hoy = Db.Hoy() });
            return Results.Ok(new { fecha = Db.Hoy(), gastos, cierres });
        });

        g.MapPost("/gastos", async (GastoDto dto, Db db, HttpContext ctx) =>
        {
            var concepto = V.Texto(dto.Concepto, "El concepto del gasto", 80);
            var monto = V.Dinero(dto.Monto, "El valor del gasto", permitirCero: false);
            await using var cn = await db.Abrir();
            var id = await cn.ExecuteScalarAsync<long>(
                "INSERT INTO gasto (fecha, usuario_id, concepto, monto) VALUES (@f, @u, @c, @m); SELECT last_insert_rowid();",
                new { f = Db.Hoy(), u = ctx.User.UsuarioId(), c = concepto, m = monto });
            return Results.Ok(new { id });
        });

        g.MapDelete("/gastos/{id:long}", async (long id, Db db, HttpContext ctx) =>
        {
            await using var cn = await db.Abrir();
            var gasto = await cn.QuerySingleOrDefaultAsync("SELECT usuario_id AS usuarioId, cierre_id AS cierreId, concepto, monto FROM gasto WHERE id = @id", new { id })
                ?? throw new NegocioException("Ese gasto no existe.", 404);
            if (gasto.cierreId is not null) throw new NegocioException("Ese gasto ya está dentro de un cierre de caja.", 409);
            if (!ctx.User.EsAdmin() && (long)gasto.usuarioId != ctx.User.UsuarioId())
                throw new NegocioException("Solo quien registró el gasto o un administrador puede borrarlo.", 403);
            await cn.ExecuteAsync("DELETE FROM gasto WHERE id = @id", new { id });
            await cn.Auditar(ctx.User.UsuarioId(), "gasto_eliminado", $"{gasto.concepto} por {(long)gasto.monto:N0}");
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/cierre", async (CierreDto dto, Db db, HttpContext ctx) =>
        {
            var baseInicial = V.Dinero(dto.BaseInicial ?? 0, "La base inicial");
            var efectivo = V.Dinero(dto.EfectivoContado, "El efectivo contado");
            var otros = V.Dinero(dto.OtrosReportados ?? 0, "Los otros ingresos");
            var observaciones = V.TextoOpc(dto.Observaciones, "Las observaciones", 400);
            string? desglose = null;
            if (dto.Desglose is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } d)
            {
                desglose = d.GetRawText();
                if (desglose.Length > 2000) throw new NegocioException("El desglose es demasiado largo.");
            }

            var cierreId = await db.Transaccion(async (cn, tx) =>
            {
                var id = await cn.ExecuteScalarAsync<long>(
                    @"INSERT INTO cierre_caja (fecha, usuario_id, base_inicial, efectivo_contado, otros_reportados, desglose, observaciones)
                      VALUES (@f, @u, @b, @e, @o, @d, @obs); SELECT last_insert_rowid();",
                    new { f = Db.Hoy(), u = ctx.User.UsuarioId(), b = baseInicial, e = efectivo, o = otros, d = desglose, obs = observaciones }, tx);
                await cn.ExecuteAsync("UPDATE gasto SET cierre_id = @id WHERE cierre_id IS NULL", new { id }, tx);
                await cn.Auditar(ctx.User.UsuarioId(), "cierre_caja", $"cierre {id}", tx);
                return id;
            });

            await using var cn2 = await db.Abrir();
            return Results.Ok(await Detalle(cn2, cierreId));
        });

        g.MapGet("/cierres", async (string? desde, string? hasta, Db db) =>
        {
            var d = FechaOpc(desde) ?? DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd");
            var h = FechaOpc(hasta) ?? Db.Hoy();
            await using var cn = await db.Abrir();
            var filas = (await cn.QueryAsync($"{SqlCierres} WHERE c.fecha BETWEEN @d AND @h ORDER BY c.id DESC LIMIT 200", new { d, h })).ToList();
            return Results.Ok(new
            {
                desde = d, hasta = h, cierres = filas,
                totales = new
                {
                    efectivo = filas.Sum(f => (long)f.efectivoContado),
                    otros = filas.Sum(f => (long)f.otrosReportados),
                    gastos = filas.Sum(f => (long)f.gastos),
                    ingreso = filas.Sum(f => (long)f.ingreso),
                },
            });
        });

        g.MapGet("/cierres/{id:long}", async (long id, Db db) =>
        {
            await using var cn = await db.Abrir();
            return Results.Ok(await Detalle(cn, id));
        });

        // Solo el administrador puede borrar un cierre mal hecho; sus gastos quedan pendientes para el siguiente.
        g.MapDelete("/cierres/{id:long}", async (long id, Db db, HttpContext ctx) =>
        {
            await db.Transaccion(async (cn, tx) =>
            {
                if (await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM cierre_caja WHERE id = @id", new { id }, tx) == 0)
                    throw new NegocioException("Ese cierre no existe.", 404);
                await cn.ExecuteAsync("UPDATE gasto SET cierre_id = NULL WHERE cierre_id = @id", new { id }, tx);
                await cn.ExecuteAsync("DELETE FROM cierre_caja WHERE id = @id", new { id }, tx);
                await cn.Auditar(ctx.User.UsuarioId(), "cierre_eliminado", $"cierre {id}", tx);
                return 0;
            });
            return Results.Ok(new { ok = true });
        }).Solo(Roles.Admin);
    }

    private static string? FechaOpc(string? t) =>
        DateTime.TryParseExact(t, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var f) ? f.ToString("yyyy-MM-dd") : null;

    private static async Task<object> Detalle(Microsoft.Data.Sqlite.SqliteConnection cn, long id)
    {
        var cierre = await cn.QuerySingleOrDefaultAsync($"{SqlCierres} WHERE c.id = @id", new { id })
            ?? throw new NegocioException("Ese cierre no existe.", 404);
        var desglose = await cn.ExecuteScalarAsync<string?>("SELECT desglose FROM cierre_caja WHERE id = @id", new { id });
        var gastos = await cn.QueryAsync(
            @"SELECT g.id, g.concepto, g.monto, u.nombre AS usuario FROM gasto g JOIN usuario u ON u.id = g.usuario_id
              WHERE g.cierre_id = @id ORDER BY g.id", new { id });
        return new { cierre, desglose = desglose is null ? (object?)null : JsonDocument.Parse(desglose).RootElement, gastos };
    }
}
