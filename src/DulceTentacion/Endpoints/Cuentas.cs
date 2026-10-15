using Dapper;
using Microsoft.Data.Sqlite;
using DulceTentacion.Infra;

namespace DulceTentacion.Endpoints;

public static class CuentasEndpoints
{
    private record AbrirDto(string? Uid, int? Personas);
    private record LlevarDto(string? Uid, string? Etiqueta);
    private record LineaPedidoDto(int? ProductoId, int? VarianteId, List<int>? Opciones, int? Cantidad, int? Persona, string? Nota);
    private record PedidoDto(string? Uid, string? Nota, List<LineaPedidoDto>? Lineas);
    private record PersonaDto(int? Persona);
    private record MotivoDto(string? Motivo);
    private record NombrePersonaDto(string? Nombre);

    // ---------------------------------------------------------------- Lectura de una cuenta completa
    public static async Task<object> Cargar(SqliteConnection cn, long id, SqliteTransaction? tx = null)
    {
        var cuenta = await cn.QuerySingleOrDefaultAsync(
            @"SELECT c.id, c.mesa_id AS mesaId, m.nombre AS mesaNombre, c.etiqueta, c.mesero_id AS meseroId, u.nombre AS mesero,
                     c.estado, c.por_cobrar AS porCobrar, c.abierta_en AS abiertaEn, c.cerrada_en AS cerradaEn, c.motivo_anulacion AS motivoAnulacion
              FROM cuenta c JOIN usuario u ON u.id = c.mesero_id LEFT JOIN mesa m ON m.id = c.mesa_id
              WHERE c.id = @id", new { id }, tx) ?? throw new NegocioException("La cuenta no existe.", 404);

        var comensales = (await cn.QueryAsync(
            "SELECT id, numero, nombre FROM comensal WHERE cuenta_id = @id ORDER BY numero", new { id }, tx)).ToList();
        var lineas = (await cn.QueryAsync(
            @"SELECT l.id, l.pedido_id AS pedidoId, l.producto_id AS productoId, l.nombre, l.detalle, l.precio, l.cantidad,
                     l.precio * l.cantidad AS subtotal, l.comensal_id AS comensalId, co.numero AS persona, l.nota,
                     l.pago_id AS pagoId, CASE WHEN l.pago_id IS NULL THEN 0 ELSE 1 END AS pagada,
                     l.anulada, l.motivo, l.creada_en AS creadaEn
              FROM linea l LEFT JOIN comensal co ON co.id = l.comensal_id
              WHERE l.cuenta_id = @id ORDER BY l.id", new { id }, tx)).ToList();
        var pagos = (await cn.QueryAsync(
            @"SELECT p.id, p.monto, p.metodo, p.recibido, p.cambio, p.etiqueta, u.nombre AS cajero, p.creado_en AS creadoEn, p.anulado
              FROM pago p JOIN usuario u ON u.id = p.cajero_id WHERE p.cuenta_id = @id ORDER BY p.id", new { id }, tx)).ToList();

        long total = 0, pagado = 0;
        var porPersona = new Dictionary<long, (long Total, long Pendiente)>();   // 0 = común
        foreach (var l in lineas)
        {
            if (l.anulada != 0) continue;
            long sub = l.subtotal;
            total += sub;
            long clave = l.persona is null ? 0 : (long)l.persona;
            var actual = porPersona.GetValueOrDefault(clave);
            porPersona[clave] = (actual.Total + sub, actual.Pendiente + (l.pagoId is null ? sub : 0));
        }
        foreach (var p in pagos) if (p.anulado == 0) pagado += (long)p.monto;

        var personas = comensales.Select(c => new
        {
            c.id, c.numero, c.nombre,
            total = porPersona.GetValueOrDefault((long)c.numero).Total,
            pendiente = porPersona.GetValueOrDefault((long)c.numero).Pendiente,
        }).ToList();
        var comun = porPersona.GetValueOrDefault(0);

        return new
        {
            cuenta, comensales = personas, lineas, pagos,
            comun = new { total = comun.Total, pendiente = comun.Pendiente },
            totales = new { total, pagado, falta = Math.Max(total - pagado, 0) },
        };
    }

    // ---------------------------------------------------------------- Apoyos
    private static async Task<long> AsegurarPersona(SqliteConnection cn, SqliteTransaction tx, long cuentaId, int numero)
    {
        var id = await cn.ExecuteScalarAsync<long?>("SELECT id FROM comensal WHERE cuenta_id = @c AND numero = @n", new { c = cuentaId, n = numero }, tx);
        if (id is not null) return id.Value;
        // se crean también las que falten antes (Persona 1, 2, …) para que la numeración no tenga huecos
        var max = await cn.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(numero), 0) FROM comensal WHERE cuenta_id = @c", new { c = cuentaId }, tx);
        for (var n = (int)max + 1; n <= numero; n++)
            await cn.ExecuteAsync("INSERT INTO comensal (cuenta_id, numero) VALUES (@c, @n)", new { c = cuentaId, n }, tx);
        return await cn.ExecuteScalarAsync<long>("SELECT id FROM comensal WHERE cuenta_id = @c AND numero = @n", new { c = cuentaId, n = numero }, tx);
    }

    private static async Task ExigirAbierta(SqliteConnection cn, SqliteTransaction tx, long id)
    {
        var estado = await cn.ExecuteScalarAsync<string?>("SELECT estado FROM cuenta WHERE id = @id", new { id }, tx)
            ?? throw new NegocioException("La cuenta no existe.", 404);
        if (estado == "pagada") throw new NegocioException("Esta cuenta ya fue pagada. Abre la mesa de nuevo para una cuenta nueva.", 409);
        if (estado == "anulada") throw new NegocioException("Esta cuenta fue anulada.", 409);
    }

    // ---------------------------------------------------------------- Rutas
    public static void MapCuentas(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // Estado de todas las mesas: lo que ve la mesera al entrar.
        g.MapGet("/mesas", async (Db db) =>
        {
            await using var cn = await db.Abrir();
            var mesas = await cn.QueryAsync(
                @"SELECT m.id, m.nombre, c.id AS cuentaId, c.mesero_id AS meseroId, u.nombre AS mesero, c.por_cobrar AS porCobrar, c.abierta_en AS desde,
                         COALESCE((SELECT SUM(l.precio * l.cantidad) FROM linea l WHERE l.cuenta_id = c.id AND l.anulada = 0), 0) AS total,
                         COALESCE((SELECT SUM(p.monto) FROM pago p WHERE p.cuenta_id = c.id AND p.anulado = 0), 0) AS pagado,
                         (SELECT COUNT(*) FROM comensal co WHERE co.cuenta_id = c.id) AS personas,
                         CASE WHEN c.id IS NULL THEN 'libre' WHEN c.por_cobrar = 1 THEN 'por_cobrar' ELSE 'ocupada' END AS estado
                  FROM mesa m
                  LEFT JOIN cuenta c ON c.mesa_id = m.id AND c.estado = 'abierta'
                  LEFT JOIN usuario u ON u.id = c.mesero_id
                  WHERE m.activa = 1 ORDER BY m.orden, m.id");
            var llevar = await cn.QueryAsync(
                @"SELECT c.id AS cuentaId, c.etiqueta AS nombre, c.mesero_id AS meseroId, u.nombre AS mesero, c.por_cobrar AS porCobrar, c.abierta_en AS desde,
                         COALESCE((SELECT SUM(l.precio * l.cantidad) FROM linea l WHERE l.cuenta_id = c.id AND l.anulada = 0), 0) AS total,
                         COALESCE((SELECT SUM(p.monto) FROM pago p WHERE p.cuenta_id = c.id AND p.anulado = 0), 0) AS pagado,
                         CASE WHEN c.por_cobrar = 1 THEN 'por_cobrar' ELSE 'ocupada' END AS estado
                  FROM cuenta c JOIN usuario u ON u.id = c.mesero_id
                  WHERE c.mesa_id IS NULL AND c.estado = 'abierta' ORDER BY c.id");
            return Results.Ok(new { mesas, llevar });
        });

        // Abrir una mesa. Si ya tiene cuenta abierta, devuelve esa misma (otra mesera puede ayudar en la mesa).
        g.MapPost("/mesas/{id:int}/abrir", async (int id, AbrirDto dto, Db db, Eventos ev, HttpContext ctx) =>
        {
            var personas = Math.Clamp(dto.Personas ?? 1, 1, 30);
            var cuentaId = await db.Transaccion(async (cn, tx) =>
            {
                if (await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mesa WHERE id = @id AND activa = 1", new { id }, tx) == 0)
                    throw new NegocioException("Esa mesa no existe.", 404);
                if (!string.IsNullOrEmpty(dto.Uid))
                {
                    var previa = await cn.ExecuteScalarAsync<long?>("SELECT id FROM cuenta WHERE uid = @u", new { u = dto.Uid }, tx);
                    if (previa is not null) return previa.Value;
                }
                var abierta = await cn.ExecuteScalarAsync<long?>("SELECT id FROM cuenta WHERE mesa_id = @id AND estado = 'abierta'", new { id }, tx);
                if (abierta is not null) return abierta.Value;
                var nueva = await cn.ExecuteScalarAsync<long>(
                    "INSERT INTO cuenta (uid, mesa_id, mesero_id) VALUES (@uid, @id, @mesero); SELECT last_insert_rowid();",
                    new { uid = string.IsNullOrEmpty(dto.Uid) ? null : dto.Uid, id, mesero = ctx.User.UsuarioId() }, tx);
                for (var n = 1; n <= personas; n++)
                    await cn.ExecuteAsync("INSERT INTO comensal (cuenta_id, numero) VALUES (@c, @n)", new { c = nueva, n }, tx);
                return nueva;
            });
            ev.Emitir("mesas");
            await using var cn2 = await db.Abrir();
            return Results.Ok(await Cargar(cn2, cuentaId));
        });

        // Pedido para llevar: no usa mesa, se identifica con el nombre del cliente.
        g.MapPost("/llevar", async (LlevarDto dto, Db db, Eventos ev, HttpContext ctx) =>
        {
            var etiqueta = V.Texto(dto.Etiqueta, "El nombre del cliente", 40);
            var cuentaId = await db.Transaccion(async (cn, tx) =>
            {
                if (!string.IsNullOrEmpty(dto.Uid))
                {
                    var previa = await cn.ExecuteScalarAsync<long?>("SELECT id FROM cuenta WHERE uid = @u", new { u = dto.Uid }, tx);
                    if (previa is not null) return previa.Value;
                }
                var nueva = await cn.ExecuteScalarAsync<long>(
                    "INSERT INTO cuenta (uid, etiqueta, mesero_id) VALUES (@uid, @e, @mesero); SELECT last_insert_rowid();",
                    new { uid = string.IsNullOrEmpty(dto.Uid) ? null : dto.Uid, e = etiqueta, mesero = ctx.User.UsuarioId() }, tx);
                await cn.ExecuteAsync("INSERT INTO comensal (cuenta_id, numero) VALUES (@c, 1)", new { c = nueva }, tx);
                return nueva;
            });
            ev.Emitir("mesas");
            await using var cn2 = await db.Abrir();
            return Results.Ok(await Cargar(cn2, cuentaId));
        });

        g.MapGet("/cuentas/{id:long}", async (long id, Db db) =>
        {
            await using var cn = await db.Abrir();
            return Results.Ok(await Cargar(cn, id));
        });

        // Envío de un pedido (una ronda). Lleva un uid: si el celular reintenta, no se duplica.
        g.MapPost("/cuentas/{id:long}/pedido", async (long id, PedidoDto dto, Db db, Eventos ev, HttpContext ctx) =>
        {
            if (dto.Lineas is not { Count: > 0 }) throw new NegocioException("El pedido no tiene productos.");
            if (dto.Lineas.Count > 100) throw new NegocioException("Un pedido admite hasta 100 renglones.");
            var nota = V.TextoOpc(dto.Nota, "La nota", 200);

            await db.Transaccion(async (cn, tx) =>
            {
                if (!string.IsNullOrEmpty(dto.Uid))
                {
                    var previo = await cn.ExecuteScalarAsync<long?>("SELECT id FROM pedido WHERE uid = @u", new { u = dto.Uid }, tx);
                    if (previo is not null) return 0;      // ya llegó antes: no hace nada
                }
                await ExigirAbierta(cn, tx, id);
                var pedidoId = await cn.ExecuteScalarAsync<long>(
                    "INSERT INTO pedido (uid, cuenta_id, mesero_id, nota) VALUES (@uid, @c, @m, @nota); SELECT last_insert_rowid();",
                    new { uid = string.IsNullOrEmpty(dto.Uid) ? null : dto.Uid, c = id, m = ctx.User.UsuarioId(), nota }, tx);

                foreach (var l in dto.Lineas)
                {
                    var cantidad = V.Entero(l.Cantidad, "La cantidad", 1, 50);
                    var prod = await cn.QuerySingleOrDefaultAsync(
                        "SELECT id, nombre, precio, activo FROM producto WHERE id = @id", new { id = l.ProductoId }, tx)
                        ?? throw new NegocioException("Uno de los productos ya no existe. Actualiza el menú.");
                    if (prod.activo == 0) throw new NegocioException($"{prod.nombre} ya no está disponible.");

                    long precio = prod.precio;
                    var partes = new List<string>();
                    var variantes = (await cn.QueryAsync("SELECT id, nombre, precio FROM variante WHERE producto_id = @p", new { p = prod.id }, tx)).ToList();
                    if (variantes.Count > 0)
                    {
                        var v = variantes.FirstOrDefault(x => (long)x.id == l.VarianteId)
                            ?? throw new NegocioException($"Elige el tamaño o la presentación de {prod.nombre}.");
                        precio = v.precio;
                        partes.Add((string)v.nombre);
                    }

                    var grupos = (await cn.QueryAsync("SELECT id, nombre, min_sel AS min, max_sel AS max FROM grupo_opcion WHERE producto_id = @p ORDER BY orden", new { p = prod.id }, tx)).ToList();
                    var elegidas = l.Opciones ?? [];
                    foreach (var gr in grupos)
                    {
                        var ops = (await cn.QueryAsync("SELECT id, nombre, precio_extra AS extra FROM opcion WHERE grupo_id = @g ORDER BY orden", new { g = gr.id }, tx)).ToList();
                        var enGrupo = ops.Where(o => elegidas.Contains((int)(long)o.id)).ToList();
                        if (enGrupo.Count < gr.min)
                            throw new NegocioException($"{prod.nombre}: elige {(gr.min == 1 ? "una opción" : $"{gr.min} opciones")} de “{gr.nombre}”.");
                        if (enGrupo.Count > gr.max)
                            throw new NegocioException($"{prod.nombre}: en “{gr.nombre}” puedes elegir máximo {gr.max}.");
                        if (enGrupo.Count > 0) partes.Add(string.Join(" + ", enGrupo.Select(o => (long)o.extra > 0 ? $"{o.nombre} (+${(long)o.extra:N0})" : (string)o.nombre)));
                        precio += enGrupo.Sum(o => (long)o.extra);
                    }

                    var persona = Math.Clamp(l.Persona ?? 1, 0, 30);   // 0 = para compartir (común)
                    long? comensalId = persona == 0 ? null : await AsegurarPersona(cn, tx, id, persona);
                    await cn.ExecuteAsync(
                        @"INSERT INTO linea (cuenta_id, pedido_id, producto_id, nombre, detalle, precio, cantidad, comensal_id, nota)
                          VALUES (@c, @p, @prod, @nombre, @detalle, @precio, @cant, @com, @nota)",
                        new
                        {
                            c = id, p = pedidoId, prod = (long)prod.id, nombre = (string)prod.nombre,
                            detalle = partes.Count == 0 ? null : string.Join(" · ", partes),
                            precio, cant = cantidad, com = comensalId, nota = V.TextoOpc(l.Nota, "La nota", 120),
                        }, tx);
                }
                // si ya habían pedido la cuenta y se agrega algo, deja de estar "por cobrar"
                await cn.ExecuteAsync("UPDATE cuenta SET por_cobrar = 0 WHERE id = @id", new { id }, tx);
                return 1;
            });
            ev.Emitir("cuenta", new { id });
            ev.Emitir("mesas");
            await using var cn2 = await db.Abrir();
            return Results.Ok(await Cargar(cn2, id));
        });

        // Cambiar a quién le pertenece un renglón (o pasarlo a "común" con persona = 0).
        g.MapPost("/lineas/{id:long}/persona", async (long id, PersonaDto dto, Db db, Eventos ev) =>
        {
            var cuentaId = await db.Transaccion(async (cn, tx) =>
            {
                var l = await cn.QuerySingleOrDefaultAsync("SELECT cuenta_id AS cuentaId, pago_id AS pagoId, anulada FROM linea WHERE id = @id", new { id }, tx)
                    ?? throw new NegocioException("Ese renglón no existe.", 404);
                if (l.pagoId is not null) throw new NegocioException("Ese renglón ya está pagado.", 409);
                if (l.anulada != 0) throw new NegocioException("Ese renglón está anulado.", 409);
                await ExigirAbierta(cn, tx, (long)l.cuentaId);
                var persona = dto.Persona ?? 0;
                long? comensal = persona <= 0 ? null : await AsegurarPersona(cn, tx, (long)l.cuentaId, Math.Min(persona, 30));
                await cn.ExecuteAsync("UPDATE linea SET comensal_id = @c WHERE id = @id", new { c = comensal, id }, tx);
                return (long)l.cuentaId;
            });
            ev.Emitir("cuenta", new { id = cuentaId });
            await using var cn2 = await db.Abrir();
            return Results.Ok(await Cargar(cn2, cuentaId));
        });

        g.MapPost("/lineas/{id:long}/anular", async (long id, MotivoDto dto, Db db, Eventos ev, HttpContext ctx) =>
        {
            var motivo = V.Texto(dto.Motivo, "El motivo", 150);
            var cuentaId = await db.Transaccion(async (cn, tx) =>
            {
                var l = await cn.QuerySingleOrDefaultAsync("SELECT cuenta_id AS cuentaId, pago_id AS pagoId, anulada, nombre FROM linea WHERE id = @id", new { id }, tx)
                    ?? throw new NegocioException("Ese renglón no existe.", 404);
                if (l.pagoId is not null) throw new NegocioException("Ese renglón ya está pagado: pídele al administrador que anule el cobro.", 409);
                if (l.anulada != 0) throw new NegocioException("Ese renglón ya estaba anulado.", 409);
                await ExigirAbierta(cn, tx, (long)l.cuentaId);
                await cn.ExecuteAsync("UPDATE linea SET anulada = 1, anulada_por = @u, motivo = @m WHERE id = @id", new { u = ctx.User.UsuarioId(), m = motivo, id }, tx);
                await cn.Auditar(ctx.User.UsuarioId(), "linea_anulada", $"{l.nombre} (cuenta {l.cuentaId}): {motivo}", tx);
                return (long)l.cuentaId;
            });
            ev.Emitir("cuenta", new { id = cuentaId });
            ev.Emitir("mesas");
            await using var cn2 = await db.Abrir();
            return Results.Ok(await Cargar(cn2, cuentaId));
        });

        g.MapPost("/cuentas/{id:long}/personas", async (long id, Db db, Eventos ev) =>
        {
            await db.Transaccion(async (cn, tx) =>
            {
                await ExigirAbierta(cn, tx, id);
                var max = await cn.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(numero), 0) FROM comensal WHERE cuenta_id = @id", new { id }, tx);
                if (max >= 30) throw new NegocioException("Una cuenta admite hasta 30 personas.");
                await cn.ExecuteAsync("INSERT INTO comensal (cuenta_id, numero) VALUES (@id, @n)", new { id, n = max + 1 }, tx);
                return 0;
            });
            ev.Emitir("cuenta", new { id });
            await using var cn2 = await db.Abrir();
            return Results.Ok(await Cargar(cn2, id));
        });

        g.MapPut("/cuentas/{id:long}/personas/{numero:int}", async (long id, int numero, NombrePersonaDto dto, Db db, Eventos ev) =>
        {
            var nombre = V.TextoOpc(dto.Nombre, "El nombre", 30);
            await using var cn = await db.Abrir();
            var filas = await cn.ExecuteAsync("UPDATE comensal SET nombre = @n WHERE cuenta_id = @id AND numero = @num", new { n = nombre, id, num = numero });
            if (filas == 0) throw new NegocioException("Esa persona no existe en la cuenta.", 404);
            ev.Emitir("cuenta", new { id });
            return Results.Ok(await Cargar(cn, id));
        });

        // La mesa pide la cuenta: queda marcada para que caja la cobre.
        g.MapPost("/cuentas/{id:long}/pedir-cuenta", async (long id, Db db, Eventos ev) =>
        {
            await db.Transaccion(async (cn, tx) =>
            {
                await ExigirAbierta(cn, tx, id);
                await cn.ExecuteAsync("UPDATE cuenta SET por_cobrar = 1 WHERE id = @id", new { id }, tx);
                return 0;
            });
            ev.Emitir("cuenta", new { id });
            ev.Emitir("mesas");
            await using var cn2 = await db.Abrir();
            return Results.Ok(await Cargar(cn2, id));
        });

        g.MapPost("/cuentas/{id:long}/anular", async (long id, MotivoDto dto, Db db, Eventos ev, HttpContext ctx) =>
        {
            var motivo = V.Texto(dto.Motivo, "El motivo", 200);
            await db.Transaccion(async (cn, tx) =>
            {
                await ExigirAbierta(cn, tx, id);
                if (await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pago WHERE cuenta_id = @id AND anulado = 0", new { id }, tx) > 0)
                    throw new NegocioException("Esta cuenta ya tiene cobros. Primero anula los cobros.", 409);
                await cn.ExecuteAsync(
                    "UPDATE cuenta SET estado = 'anulada', cerrada_en = @ahora, motivo_anulacion = @m, anulada_por = @u WHERE id = @id",
                    new { ahora = Db.Ahora(), m = motivo, u = ctx.User.UsuarioId(), id }, tx);
                await cn.Auditar(ctx.User.UsuarioId(), "cuenta_anulada", $"cuenta {id}: {motivo}", tx);
                return 0;
            });
            ev.Emitir("cuenta", new { id });
            ev.Emitir("mesas");
            await using var cn2 = await db.Abrir();
            return Results.Ok(await Cargar(cn2, id));
        }).Solo(Roles.Admin);
    }
}
