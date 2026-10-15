using Dapper;

namespace DulceTentacion.Infra;

/// <summary>
/// Solo para la versión de demostración: una semana de movimiento de ejemplo (cuentas pagadas, encuestas,
/// gastos y cierres de caja) y algunas mesas abiertas hoy, para que las estadísticas y el Excel no salgan vacíos.
/// </summary>
public static class SemillaDemo
{
    private sealed record Prod(long Id, string Nombre, long Precio, long Categoria);
    private sealed record Var(long Id, long ProductoId, string Nombre, long Precio);
    private sealed record Grupo(long Id, long ProductoId, string Nombre, long Min);
    private sealed record Op(long Id, long GrupoId, string Nombre, long Extra);

    private static readonly string[] Comentarios =
    [
        "Todo muy rico y rápido.", "Excelente atención, volveremos.", "El caldo estuvo delicioso.",
        "Muy buen café y buen ambiente.", "El pan recién hecho, buenísimo.", "Un poco de espera, pero valió la pena.",
        "Gente muy amable.", "Los desayunos son abundantes.",
    ];

    private static readonly string[] Conceptos = ["Hielo", "Domicilio", "Servilletas", "Gas", "Verduras", "Huevos", "Leche", "Bolsas"];

    public static async Task Cargar(Db db, ILogger log)
    {
        var azar = new Random(2026);
        await using var cn = await db.Abrir();
        await using var tx = cn.BeginTransaction();

        var productos = (await cn.QueryAsync<Prod>(
            "SELECT id AS Id, nombre AS Nombre, precio AS Precio, categoria_id AS Categoria FROM producto WHERE activo = 1", transaction: tx)).ToList();
        var variantes = (await cn.QueryAsync<Var>(
            "SELECT id AS Id, producto_id AS ProductoId, nombre AS Nombre, precio AS Precio FROM variante", transaction: tx)).ToList();
        var grupos = (await cn.QueryAsync<Grupo>(
            "SELECT id AS Id, producto_id AS ProductoId, nombre AS Nombre, min_sel AS Min FROM grupo_opcion", transaction: tx)).ToList();
        var opciones = (await cn.QueryAsync<Op>(
            "SELECT id AS Id, grupo_id AS GrupoId, nombre AS Nombre, precio_extra AS Extra FROM opcion ORDER BY orden", transaction: tx)).ToList();
        var categorias = (await cn.QueryAsync<(long Id, string Nombre)>(
            "SELECT id, nombre FROM categoria", transaction: tx)).ToDictionary(c => c.Id, c => c.Nombre);
        var meseros = (await cn.QueryAsync<(long Id, string Rol)>("SELECT id, rol FROM usuario WHERE activo = 1", transaction: tx)).ToList();
        var mesas = (await cn.QueryAsync<long>("SELECT id FROM mesa WHERE activa = 1 ORDER BY orden", transaction: tx)).ToList();
        var admin = meseros.First(u => u.Rol == Roles.Admin).Id;
        var atienden = meseros.Select(u => u.Id).ToList();

        // Más peso a desayunos, bebidas calientes y amasijos: es lo que más se pide en una cafetería.
        int Peso(Prod p) => categorias.GetValueOrDefault(p.Categoria) switch
        {
            "Desayunos" or "Bebidas Calientes" or "Amasijos y Pastelería" => 6,
            "Bebidas Frías" => 3,
            _ => 1,
        };
        var ruleta = productos.SelectMany(p => Enumerable.Repeat(p, Peso(p))).ToList();

        (long Producto, string Nombre, string? Detalle, long Precio, int Cantidad) ArmarLinea()
        {
            var p = ruleta[azar.Next(ruleta.Count)];
            long precio = p.Precio;
            var partes = new List<string>();
            var vs = variantes.Where(v => v.ProductoId == p.Id).ToList();
            if (vs.Count > 0)
            {
                // para el pan rollo y similares, las presentaciones chicas son las más pedidas
                var v = vs[Math.Min(azar.Next(vs.Count), azar.Next(vs.Count))];
                precio = v.Precio; partes.Add(v.Nombre);
            }
            foreach (var g in grupos.Where(g => g.ProductoId == p.Id))
            {
                var ops = opciones.Where(o => o.GrupoId == g.Id).ToList();
                if (ops.Count == 0 || g.Min == 0) continue;
                var o = ops[azar.Next(ops.Count)];
                precio += o.Extra;
                partes.Add(o.Extra > 0 ? $"{o.Nombre} (+${o.Extra:N0})" : o.Nombre);
            }
            var cantidad = azar.Next(10) < 7 ? 1 : 2;
            return (p.Id, p.Nombre, partes.Count == 0 ? null : string.Join(" · ", partes), precio, cantidad);
        }

        static string Hora(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss");
        var hoy = DateTime.Now.Date;

        for (var atras = 6; atras >= 0; atras--)
        {
            var dia = hoy.AddDays(-atras);
            var cuentasDia = azar.Next(14, 26) + (dia.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 8 : 0);
            long efectivoDia = 0, otrosDia = 0;

            for (var i = 0; i < cuentasDia; i++)
            {
                // más movimiento a la hora del desayuno y a media mañana
                var hora = azar.Next(10) < 5 ? 7 + azar.Next(3) : 9 + azar.Next(7);
                var abre = dia.AddHours(hora).AddMinutes(azar.Next(60));
                if (atras == 0 && abre > DateTime.Now.AddMinutes(-20)) continue;   // nada "en el futuro" hoy
                var cierra = abre.AddMinutes(azar.Next(18, 55));
                var mesero = atienden[azar.Next(atienden.Count)];
                var llevar = azar.Next(10) == 0;
                long? mesa = llevar ? null : mesas[azar.Next(mesas.Count)];

                var cuenta = await cn.ExecuteScalarAsync<long>(
                    @"INSERT INTO cuenta (mesa_id, etiqueta, mesero_id, estado, abierta_en, cerrada_en)
                      VALUES (@mesa, @etq, @mesero, 'pagada', @abre, @cierra); SELECT last_insert_rowid();",
                    new { mesa, etq = llevar ? "Cliente " + (i + 1) : null, mesero, abre = Hora(abre), cierra = Hora(cierra) }, tx);
                await cn.ExecuteAsync("INSERT INTO comensal (cuenta_id, numero) VALUES (@c, 1)", new { c = cuenta }, tx);
                var pedido = await cn.ExecuteScalarAsync<long>(
                    "INSERT INTO pedido (cuenta_id, mesero_id, creado_en) VALUES (@c, @m, @t); SELECT last_insert_rowid();",
                    new { c = cuenta, m = mesero, t = Hora(abre) }, tx);

                var lineas = Enumerable.Range(0, azar.Next(1, 6)).Select(_ => ArmarLinea()).ToList();
                var total = lineas.Sum(l => l.Precio * l.Cantidad);
                var metodo = azar.Next(10) switch { < 6 => "efectivo", < 8 => "transferencia", _ => "tarjeta" };
                var recibido = metodo == "efectivo" ? (long)Math.Ceiling(total / 1000.0) * 1000 + (azar.Next(3) == 0 ? 5000 : 0) : total;
                var pago = await cn.ExecuteScalarAsync<long>(
                    @"INSERT INTO pago (cuenta_id, monto, metodo, recibido, cambio, etiqueta, cajero_id, creado_en)
                      VALUES (@c, @monto, @metodo, @rec, @cambio, 'Cuenta completa', @cajero, @t); SELECT last_insert_rowid();",
                    new { c = cuenta, monto = total, metodo, rec = recibido, cambio = recibido - total, cajero = admin, t = Hora(cierra) }, tx);
                foreach (var l in lineas)
                    await cn.ExecuteAsync(
                        @"INSERT INTO linea (cuenta_id, pedido_id, producto_id, nombre, detalle, precio, cantidad, pago_id, creada_en)
                          VALUES (@c, @p, @prod, @n, @d, @precio, @cant, @pago, @t)",
                        new { c = cuenta, p = pedido, prod = l.Producto, n = l.Nombre, d = l.Detalle, precio = l.Precio, cant = l.Cantidad, pago, t = Hora(abre) }, tx);
                if (metodo == "efectivo") efectivoDia += total; else otrosDia += total;

                if (azar.Next(10) < 4)
                {
                    int Nota() => azar.Next(10) switch { < 5 => 5, < 8 => 4, < 9 => 3, _ => azar.Next(2, 6) };
                    await cn.ExecuteAsync(
                        "INSERT INTO encuesta (cuenta_id, mesero_id, atencion, comida, comentario, creada_en) VALUES (@c, @m, @a, @co, @t, @f)",
                        new { c = cuenta, m = mesero, a = Nota(), co = Nota(), t = azar.Next(10) < 3 ? Comentarios[azar.Next(Comentarios.Length)] : null, f = Hora(cierra) }, tx);
                }
            }

            // Cierre de caja de los días anteriores (hoy se deja sin cerrar para poder probarlo).
            if (atras > 0)
            {
                var cierre = await cn.ExecuteScalarAsync<long>(
                    @"INSERT INTO cierre_caja (fecha, usuario_id, base_inicial, efectivo_contado, otros_reportados, observaciones, creado_en)
                      VALUES (@f, @u, 100000, @ef, @ot, @obs, @t); SELECT last_insert_rowid();",
                    new
                    {
                        f = dia.ToString("yyyy-MM-dd"), u = admin, ef = 100000 + efectivoDia - azar.Next(60, 140) * 1000, ot = otrosDia,
                        obs = azar.Next(4) == 0 ? "Todo cuadró." : null, t = Hora(dia.AddHours(16)),
                    }, tx);
                for (var g = 0; g < azar.Next(2, 4); g++)
                    await cn.ExecuteAsync(
                        "INSERT INTO gasto (fecha, usuario_id, concepto, monto, cierre_id, creado_en) VALUES (@f, @u, @c, @m, @cierre, @t)",
                        new { f = dia.ToString("yyyy-MM-dd"), u = admin, c = Conceptos[azar.Next(Conceptos.Length)], m = azar.Next(6, 40) * 1000L, cierre, t = Hora(dia.AddHours(11)) }, tx);
            }
        }

        // Mesas abiertas ahora, para ver el tablero con movimiento.
        var libres = mesas.OrderBy(_ => azar.Next()).Take(3).ToList();
        for (var k = 0; k < libres.Count; k++)
        {
            var mesero = atienden[azar.Next(atienden.Count)];
            var abre = DateTime.Now.AddMinutes(-azar.Next(5, 35));
            var cuenta = await cn.ExecuteScalarAsync<long>(
                "INSERT INTO cuenta (mesa_id, mesero_id, estado, por_cobrar, abierta_en) VALUES (@mesa, @m, 'abierta', @pc, @t); SELECT last_insert_rowid();",
                new { mesa = libres[k], m = mesero, pc = k == 0 ? 1 : 0, t = Hora(abre) }, tx);
            await cn.ExecuteAsync("INSERT INTO comensal (cuenta_id, numero) VALUES (@c, 1)", new { c = cuenta }, tx);
            var pedido = await cn.ExecuteScalarAsync<long>(
                "INSERT INTO pedido (cuenta_id, mesero_id, creado_en) VALUES (@c, @m, @t); SELECT last_insert_rowid();",
                new { c = cuenta, m = mesero, t = Hora(abre) }, tx);
            foreach (var l in Enumerable.Range(0, azar.Next(2, 5)).Select(_ => ArmarLinea()))
                await cn.ExecuteAsync(
                    @"INSERT INTO linea (cuenta_id, pedido_id, producto_id, nombre, detalle, precio, cantidad, creada_en)
                      VALUES (@c, @p, @prod, @n, @d, @precio, @cant, @t)",
                    new { c = cuenta, p = pedido, prod = l.Producto, n = l.Nombre, d = l.Detalle, precio = l.Precio, cant = l.Cantidad, t = Hora(abre) }, tx);
        }

        await MenuTocar(cn, tx);
        await tx.CommitAsync();
        log.LogInformation("Datos de demostración cargados (una semana de movimiento).");
    }

    private static Task MenuTocar(Microsoft.Data.Sqlite.SqliteConnection cn, Microsoft.Data.Sqlite.SqliteTransaction tx) =>
        cn.ExecuteAsync("INSERT INTO meta (clave, valor) VALUES ('menu_version', @v) ON CONFLICT(clave) DO UPDATE SET valor = @v",
            new { v = DateTime.Now.Ticks.ToString() }, tx);
}
