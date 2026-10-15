using System.Globalization;
using ClosedXML.Excel;
using Dapper;
using DulceTentacion.Infra;

namespace DulceTentacion.Endpoints;

/// <summary>Descargas en Excel (.xlsx) para el dueño: estadísticas completas y cierres de caja.</summary>
public static class ExcelEndpoints
{
    private const string TipoXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static string Fecha(string? t, string defecto) =>
        DateTime.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f.ToString("yyyy-MM-dd") : defecto;

    private static (string Desde, string Hasta) Rango(string? desde, string? hasta, int diasAtras)
    {
        var d = Fecha(desde, DateTime.Now.AddDays(-diasAtras).ToString("yyyy-MM-dd"));
        var h = Fecha(hasta, Db.Hoy());
        return string.CompareOrdinal(d, h) > 0 ? (h, d) : (d, h);
    }

    // Una hoja con encabezados en negrita, filas, columnas de dinero con formato y ancho ajustado.
    private static void Hoja(XLWorkbook wb, string nombre, string[] encabezados, IEnumerable<object?[]> filas, params int[] columnasDinero)
    {
        var ws = wb.AddWorksheet(nombre);
        for (var c = 0; c < encabezados.Length; c++)
        {
            var celda = ws.Cell(1, c + 1);
            celda.Value = encabezados[c];
            celda.Style.Font.Bold = true;
            celda.Style.Fill.BackgroundColor = XLColor.FromHtml("#F3E2B0");
        }
        var r = 2;
        foreach (var fila in filas)
        {
            for (var c = 0; c < fila.Length; c++)
            {
                var celda = ws.Cell(r, c + 1);
                switch (fila[c])
                {
                    case long l: celda.Value = l; break;
                    case int i: celda.Value = i; break;
                    case double d: celda.Value = d; break;
                    case null: break;
                    default: celda.Value = fila[c]!.ToString(); break;
                }
                if (columnasDinero.Contains(c + 1)) celda.Style.NumberFormat.Format = "$ #,##0";
            }
            r++;
        }
        ws.SheetView.FreezeRows(1);
        try { ws.Columns().AdjustToContents(); } catch { /* servidor sin fuentes: se deja el ancho por defecto */ }
    }

    private static List<object?[]> Filas(IEnumerable<dynamic> rows) =>
        rows.Select(x => ((IDictionary<string, object>)x).Values.Select(v => (object?)v).ToArray()).ToList();

    private static IResult Entregar(XLWorkbook wb, string nombre)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return Results.File(ms.ToArray(), TipoXlsx, nombre);
    }

    private const string SqlCierres =
        @"SELECT c.fecha, u.nombre, c.base_inicial, c.efectivo_contado, c.otros_reportados,
                 COALESCE((SELECT SUM(g.monto) FROM gasto g WHERE g.cierre_id = c.id), 0),
                 c.efectivo_contado + c.otros_reportados - c.base_inicial + COALESCE((SELECT SUM(g.monto) FROM gasto g WHERE g.cierre_id = c.id), 0),
                 COALESCE(c.observaciones, '')
          FROM cierre_caja c JOIN usuario u ON u.id = c.usuario_id WHERE c.fecha BETWEEN @d AND @h ORDER BY c.id";

    private static readonly string[] EncCierres = ["Fecha", "Cerró", "Base inicial", "Efectivo contado", "Otros reportados", "Gastos", "Ingreso", "Observaciones"];

    public static void MapExcel(this WebApplication app)
    {
        // ---------- Estadísticas completas (solo administración)
        app.MapGet("/api/admin/excel", async (string? desde, string? hasta, Db db) =>
        {
            var (d, h) = Rango(desde, hasta, 6);
            var p = new { d, h };
            await using var cn = await db.Abrir();
            const string filtro = "c.estado <> 'anulada' AND l.anulada = 0 AND date(c.abierta_en) BETWEEN @d AND @h";
            using var wb = new XLWorkbook();

            var resumen = await cn.QuerySingleAsync(
                $@"SELECT COUNT(DISTINCT c.id) AS cuentas, COALESCE(SUM(l.cantidad), 0) AS unidades, COALESCE(SUM(l.precio * l.cantidad), 0) AS pedido
                   FROM linea l JOIN cuenta c ON c.id = l.cuenta_id WHERE {filtro}", p);
            var caja = await cn.QuerySingleAsync(
                @"SELECT COUNT(*) AS cierres, COALESCE(SUM(efectivo_contado), 0) AS efectivo, COALESCE(SUM(otros_reportados), 0) AS otros,
                         COALESCE(SUM(base_inicial), 0) AS baseInicial,
                         COALESCE((SELECT SUM(g.monto) FROM gasto g WHERE g.cierre_id IN (SELECT id FROM cierre_caja WHERE fecha BETWEEN @d AND @h)), 0) AS gastos
                  FROM cierre_caja WHERE fecha BETWEEN @d AND @h", p);
            long cuentas = resumen.cuentas, pedido = resumen.pedido;
            long ingreso = (long)caja.efectivo + (long)caja.otros + (long)caja.gastos - (long)caja.baseInicial;
            Hoja(wb, "Resumen", ["Concepto", "Valor"],
            [
                ["Periodo", $"{d} a {h}"],
                ["Cuentas atendidas", cuentas],
                ["Productos pedidos", (long)resumen.unidades],
                ["Total pedido", pedido],
                ["Cuenta promedio", cuentas == 0 ? 0L : pedido / cuentas],
                ["Cierres de caja", (long)caja.cierres],
                ["Efectivo contado (cierres)", (long)caja.efectivo],
                ["Otros reportados (cierres)", (long)caja.otros],
                ["Gastos", (long)caja.gastos],
                ["Base inicial", (long)caja.baseInicial],
                ["Ingreso = efectivo + otros + gastos - base", ingreso],
            ], 2);

            Hoja(wb, "Productos", ["Producto", "Categoría", "Cantidad", "Valor pedido"], Filas(await cn.QueryAsync(
                $@"SELECT l.nombre, COALESCE(ca.nombre, 'Otros'), SUM(l.cantidad), SUM(l.precio * l.cantidad)
                   FROM linea l JOIN cuenta c ON c.id = l.cuenta_id
                   LEFT JOIN producto pr ON pr.id = l.producto_id LEFT JOIN categoria ca ON ca.id = pr.categoria_id
                   WHERE {filtro} GROUP BY l.nombre, ca.nombre ORDER BY SUM(l.cantidad) DESC", p)), 4);

            Hoja(wb, "Categorías", ["Categoría", "Cantidad", "Valor pedido"], Filas(await cn.QueryAsync(
                $@"SELECT COALESCE(ca.nombre, 'Otros'), SUM(l.cantidad), SUM(l.precio * l.cantidad)
                   FROM linea l JOIN cuenta c ON c.id = l.cuenta_id
                   LEFT JOIN producto pr ON pr.id = l.producto_id LEFT JOIN categoria ca ON ca.id = pr.categoria_id
                   WHERE {filtro} GROUP BY ca.id ORDER BY SUM(l.precio * l.cantidad) DESC", p)), 3);

            Hoja(wb, "Por hora", ["Hora", "Cantidad", "Valor pedido"], Filas(await cn.QueryAsync(
                $@"SELECT CAST(strftime('%H', l.creada_en) AS INTEGER), SUM(l.cantidad), SUM(l.precio * l.cantidad)
                   FROM linea l JOIN cuenta c ON c.id = l.cuenta_id WHERE {filtro} GROUP BY 1 ORDER BY 1", p)), 3);

            Hoja(wb, "Por día", ["Fecha", "Cuentas", "Cantidad", "Valor pedido"], Filas(await cn.QueryAsync(
                $@"SELECT date(c.abierta_en), COUNT(DISTINCT c.id), SUM(l.cantidad), SUM(l.precio * l.cantidad)
                   FROM linea l JOIN cuenta c ON c.id = l.cuenta_id WHERE {filtro} GROUP BY 1 ORDER BY 1", p)), 4);

            Hoja(wb, "Por mesera", ["Mesera/o", "Cuentas", "Cantidad", "Valor pedido"], Filas(await cn.QueryAsync(
                $@"SELECT u.nombre, COUNT(DISTINCT c.id), SUM(l.cantidad), SUM(l.precio * l.cantidad)
                   FROM linea l JOIN cuenta c ON c.id = l.cuenta_id JOIN usuario u ON u.id = c.mesero_id
                   WHERE {filtro} GROUP BY u.id ORDER BY 4 DESC", p)), 4);

            Hoja(wb, "Encuestas por mesera", ["Mesera/o", "Encuestas", "Atención (1-5)", "Comida (1-5)"], Filas(await cn.QueryAsync(
                @"SELECT u.nombre, COUNT(e.id), ROUND(AVG(e.atencion), 2), ROUND(AVG(e.comida), 2)
                  FROM usuario u LEFT JOIN encuesta e ON e.mesero_id = u.id AND date(e.creada_en) BETWEEN @d AND @h
                  WHERE u.activo = 1 GROUP BY u.id ORDER BY 2 DESC, u.nombre", p)));

            Hoja(wb, "Comentarios", ["Fecha", "Mesera/o", "Atención", "Comida", "Comentario"], Filas(await cn.QueryAsync(
                @"SELECT e.creada_en, COALESCE(u.nombre, ''), e.atencion, e.comida, e.comentario
                  FROM encuesta e LEFT JOIN usuario u ON u.id = e.mesero_id
                  WHERE e.comentario IS NOT NULL AND date(e.creada_en) BETWEEN @d AND @h ORDER BY e.id DESC", p)));

            Hoja(wb, "Cierres de caja", EncCierres, Filas(await cn.QueryAsync(SqlCierres, p)), 3, 4, 5, 6, 7);

            Hoja(wb, "Gastos", ["Fecha", "Registró", "Concepto", "Valor"], Filas(await cn.QueryAsync(
                @"SELECT g.fecha, u.nombre, g.concepto, g.monto
                  FROM gasto g JOIN usuario u ON u.id = g.usuario_id WHERE g.fecha BETWEEN @d AND @h ORDER BY g.id", p)), 4);

            Hoja(wb, "Detalle de pedidos",
                ["Fecha y hora", "Cuenta", "Mesa o cliente", "Mesera/o", "Producto", "Detalle", "Cantidad", "Precio", "Subtotal", "Estado de la cuenta"],
                Filas(await cn.QueryAsync(
                    $@"SELECT l.creada_en, c.id, COALESCE(m.nombre, 'Para llevar: ' || COALESCE(c.etiqueta, '')), u.nombre,
                              l.nombre, COALESCE(l.detalle, ''), l.cantidad, l.precio, l.precio * l.cantidad, c.estado
                       FROM linea l JOIN cuenta c ON c.id = l.cuenta_id JOIN usuario u ON u.id = c.mesero_id LEFT JOIN mesa m ON m.id = c.mesa_id
                       WHERE {filtro} ORDER BY l.id", p)), 8, 9);

            return Entregar(wb, $"Dulce_Tentacion_{d}_a_{h}.xlsx");
        }).Solo(Roles.Admin);

        // ---------- Cierres de caja (administración y caja)
        app.MapGet("/api/caja/excel", async (string? desde, string? hasta, Db db) =>
        {
            var (d, h) = Rango(desde, hasta, 29);
            var p = new { d, h };
            await using var cn = await db.Abrir();
            using var wb = new XLWorkbook();
            Hoja(wb, "Cierres de caja", EncCierres, Filas(await cn.QueryAsync(SqlCierres, p)), 3, 4, 5, 6, 7);
            Hoja(wb, "Gastos", ["Fecha", "Registró", "Concepto", "Valor", "Cierre"], Filas(await cn.QueryAsync(
                @"SELECT g.fecha, u.nombre, g.concepto, g.monto, COALESCE(g.cierre_id, 0)
                  FROM gasto g JOIN usuario u ON u.id = g.usuario_id WHERE g.fecha BETWEEN @d AND @h ORDER BY g.id", p)), 4);
            return Entregar(wb, $"Caja_Dulce_Tentacion_{d}_a_{h}.xlsx");
        }).Solo(Roles.Admin, Roles.Cajero);
    }
}
