using Dapper;
using DulceTentacion.Infra;

namespace DulceTentacion.Endpoints;

public static class MenuEndpoints
{
    /// <summary>Todo el menú activo en una sola respuesta: el celular lo guarda y la búsqueda no necesita red.</summary>
    public static async Task<object> CargarMenu(Db db, bool incluirInactivos = false)
    {
        await using var cn = await db.Abrir();
        var activo = incluirInactivos ? "1 = 1" : "activo = 1";
        var categorias = (await cn.QueryAsync(
            $"SELECT id, nombre, icono, orden, activa FROM categoria {(incluirInactivos ? "" : "WHERE activa = 1")} ORDER BY orden, id")).ToList();
        var productos = (await cn.QueryAsync(
            $@"SELECT p.id, p.categoria_id AS categoriaId, p.seccion, p.nombre, p.precio, p.activo, p.orden
               FROM producto p JOIN categoria c ON c.id = p.categoria_id
               WHERE {(incluirInactivos ? "1 = 1" : "p.activo = 1 AND c.activa = 1")} ORDER BY p.orden, p.id")).ToList();
        var variantes = (await cn.QueryAsync(
            "SELECT id, producto_id AS productoId, nombre, precio FROM variante ORDER BY producto_id, orden, id")).ToList();
        var grupos = (await cn.QueryAsync(
            "SELECT id, producto_id AS productoId, nombre, min_sel AS min, max_sel AS max FROM grupo_opcion ORDER BY producto_id, orden, id")).ToList();
        var opciones = (await cn.QueryAsync(
            "SELECT id, grupo_id AS grupoId, nombre, precio_extra AS extra FROM opcion ORDER BY grupo_id, orden, id")).ToList();

        var variantesPor = variantes.GroupBy(v => (long)v.productoId).ToDictionary(x => x.Key, x => x.Select(v => new { v.id, v.nombre, v.precio }).ToList());
        var opcionesPor = opciones.GroupBy(o => (long)o.grupoId).ToDictionary(x => x.Key, x => x.Select(o => new { o.id, o.nombre, o.extra }).ToList());
        var gruposPor = grupos.GroupBy(gr => (long)gr.productoId).ToDictionary(x => x.Key,
            x => x.Select(gr => new { gr.id, gr.nombre, gr.min, gr.max, opciones = opcionesPor.GetValueOrDefault((long)gr.id) ?? [] }).ToList());

        var lista = productos.Select(p => new
        {
            p.id, p.categoriaId, p.seccion, p.nombre, p.precio, p.activo,
            variantes = variantesPor.GetValueOrDefault((long)p.id) ?? [],
            grupos = gruposPor.GetValueOrDefault((long)p.id) ?? [],
        }).ToList();

        // Lo más pedido en los últimos 30 días (para el botón "Más pedidos" de las meseras)
        var populares = (await cn.QueryAsync<long>(
            @"SELECT l.producto_id FROM linea l JOIN cuenta c ON c.id = l.cuenta_id
              WHERE l.anulada = 0 AND l.producto_id IS NOT NULL AND c.estado <> 'anulada' AND c.abierta_en >= datetime('now', 'localtime', '-30 days')
              GROUP BY l.producto_id ORDER BY SUM(l.cantidad) DESC LIMIT 14")).ToList();

        var version = await cn.ExecuteScalarAsync<string?>("SELECT valor FROM meta WHERE clave = 'menu_version'") ?? "0";
        return new { version, categorias, productos = lista, populares };
    }

    public static async Task Tocar(Microsoft.Data.Sqlite.SqliteConnection cn, Microsoft.Data.Sqlite.SqliteTransaction? tx = null) =>
        await cn.ExecuteAsync(
            "INSERT INTO meta (clave, valor) VALUES ('menu_version', @v) ON CONFLICT(clave) DO UPDATE SET valor = @v",
            new { v = DateTime.Now.Ticks.ToString() }, tx);

    public static void MapMenu(this WebApplication app)
    {
        app.MapGet("/api/menu", async (Db db) => Results.Ok(await CargarMenu(db))).RequireAuthorization();
    }
}
