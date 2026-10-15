using System.Globalization;
using Dapper;
using DulceTentacion.Infra;

namespace DulceTentacion.Endpoints;

public static class EstadisticasEndpoints
{
    private static string Fecha(string? t, string defecto) =>
        DateTime.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f.ToString("yyyy-MM-dd") : defecto;

    public static void MapEstadisticas(this WebApplication app)
    {
        // Lo que se pide y se vende sale de los pedidos; lo que entra a la caja sale SOLO de los cierres.
        app.MapGet("/api/estadisticas", async (string? desde, string? hasta, Db db) =>
        {
            var d = Fecha(desde, DateTime.Now.AddDays(-6).ToString("yyyy-MM-dd"));
            var h = Fecha(hasta, Db.Hoy());
            if (string.CompareOrdinal(d, h) > 0) (d, h) = (h, d);
            var p = new { d, h };

            await using var cn = await db.Abrir();
            const string filtro = "c.estado <> 'anulada' AND l.anulada = 0 AND date(c.abierta_en) BETWEEN @d AND @h";

            var resumen = await cn.QuerySingleAsync(
                $@"SELECT COUNT(DISTINCT c.id) AS cuentas, COALESCE(SUM(l.cantidad), 0) AS unidades,
                          COALESCE(SUM(l.precio * l.cantidad), 0) AS pedido
                   FROM linea l JOIN cuenta c ON c.id = l.cuenta_id WHERE {filtro}", p);
            long cuentas = resumen.cuentas, pedido = resumen.pedido;

            var productos = await cn.QueryAsync(
                $@"SELECT COALESCE(l.producto_id, 0) AS id, l.nombre, SUM(l.cantidad) AS cantidad, SUM(l.precio * l.cantidad) AS valor
                   FROM linea l JOIN cuenta c ON c.id = l.cuenta_id WHERE {filtro}
                   GROUP BY COALESCE(l.producto_id, 0), l.nombre ORDER BY cantidad DESC, valor DESC LIMIT 20", p);

            var categorias = await cn.QueryAsync(
                $@"SELECT COALESCE(ca.nombre, 'Otros') AS nombre, COALESCE(ca.icono, '') AS icono,
                          SUM(l.cantidad) AS cantidad, SUM(l.precio * l.cantidad) AS valor
                   FROM linea l JOIN cuenta c ON c.id = l.cuenta_id
                   LEFT JOIN producto pr ON pr.id = l.producto_id LEFT JOIN categoria ca ON ca.id = pr.categoria_id
                   WHERE {filtro} GROUP BY ca.id ORDER BY valor DESC", p);

            var porHora = await cn.QueryAsync(
                $@"SELECT CAST(strftime('%H', l.creada_en) AS INTEGER) AS hora, SUM(l.cantidad) AS cantidad, SUM(l.precio * l.cantidad) AS valor
                   FROM linea l JOIN cuenta c ON c.id = l.cuenta_id WHERE {filtro} GROUP BY hora ORDER BY hora", p);

            var porDia = await cn.QueryAsync(
                $@"SELECT date(c.abierta_en) AS fecha, COUNT(DISTINCT c.id) AS cuentas, SUM(l.cantidad) AS cantidad, SUM(l.precio * l.cantidad) AS valor
                   FROM linea l JOIN cuenta c ON c.id = l.cuenta_id WHERE {filtro} GROUP BY fecha ORDER BY fecha", p);

            var porMesero = await cn.QueryAsync(
                $@"SELECT u.nombre, COUNT(DISTINCT c.id) AS cuentas, SUM(l.cantidad) AS cantidad, SUM(l.precio * l.cantidad) AS valor
                   FROM linea l JOIN cuenta c ON c.id = l.cuenta_id JOIN usuario u ON u.id = c.mesero_id
                   WHERE {filtro} GROUP BY u.id ORDER BY valor DESC", p);

            var porMetodo = await cn.QueryAsync(
                @"SELECT pa.metodo, COUNT(*) AS pagos, SUM(pa.monto) AS valor
                  FROM pago pa WHERE pa.anulado = 0 AND date(pa.creado_en) BETWEEN @d AND @h GROUP BY pa.metodo ORDER BY valor DESC", p);

            var encuestas = await cn.QuerySingleAsync(
                @"SELECT COUNT(*) AS total, ROUND(AVG(atencion), 2) AS atencion, ROUND(AVG(comida), 2) AS comida
                  FROM encuesta WHERE date(creada_en) BETWEEN @d AND @h", p);
            var encuestasPorMesera = await cn.QueryAsync(
                @"SELECT u.id, u.nombre, COUNT(e.id) AS total, ROUND(AVG(e.atencion), 2) AS atencion, ROUND(AVG(e.comida), 2) AS comida
                  FROM usuario u LEFT JOIN encuesta e ON e.mesero_id = u.id AND date(e.creada_en) BETWEEN @d AND @h
                  WHERE u.activo = 1 GROUP BY u.id ORDER BY total DESC, u.nombre", p);
            var comentarios = await cn.QueryAsync(
                @"SELECT e.atencion, e.comida, e.comentario, e.creada_en AS fecha, u.nombre AS mesero
                  FROM encuesta e LEFT JOIN usuario u ON u.id = e.mesero_id
                  WHERE e.comentario IS NOT NULL AND date(e.creada_en) BETWEEN @d AND @h ORDER BY e.id DESC LIMIT 40", p);

            var caja = await cn.QuerySingleAsync(
                @"SELECT COUNT(*) AS cierres, COALESCE(SUM(efectivo_contado), 0) AS efectivo, COALESCE(SUM(otros_reportados), 0) AS otros,
                         COALESCE(SUM(base_inicial), 0) AS baseInicial,
                         COALESCE((SELECT SUM(g.monto) FROM gasto g WHERE g.cierre_id IN
                                   (SELECT id FROM cierre_caja WHERE fecha BETWEEN @d AND @h)), 0) AS gastos
                  FROM cierre_caja WHERE fecha BETWEEN @d AND @h", p);
            long ingreso = (long)caja.efectivo + (long)caja.otros + (long)caja.gastos - (long)caja.baseInicial;

            return Results.Ok(new
            {
                desde = d, hasta = h,
                resumen = new { cuentas, unidades = (long)resumen.unidades, pedido, ticketPromedio = cuentas == 0 ? 0 : pedido / cuentas },
                productos, categorias, porHora, porDia, porMesero, porMetodo,
                encuestas = new { encuestas.total, encuestas.atencion, encuestas.comida, porMesera = encuestasPorMesera, comentarios },
                caja = new { caja.cierres, caja.efectivo, caja.otros, caja.gastos, caja.baseInicial, ingreso },
            });
        }).Solo(Roles.Admin);
    }
}
