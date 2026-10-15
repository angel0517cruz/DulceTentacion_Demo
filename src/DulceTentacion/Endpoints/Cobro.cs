using Dapper;
using Microsoft.Data.Sqlite;
using DulceTentacion.Infra;

namespace DulceTentacion.Endpoints;

/// <summary>
/// Cobro de una cuenta compartida. La cuenta es una sola, pero se puede pagar por partes:
/// por personas, por productos, por un monto libre o todo de una vez. Siempre se ve Total · Pagado · Falta.
/// </summary>
public static class CobroEndpoints
{
    private record CobrarDto(string? Uid, string? Modo, List<int>? Personas, List<long>? Lineas, long? Monto, string? Metodo, long? Recibido, string? Etiqueta);
    private record MotivoDto(string? Motivo);
    private record EncuestaDto(int? Atencion, int? Comida, string? Comentario);

    private static async Task<(long Total, long Pagado)> Saldos(SqliteConnection cn, SqliteTransaction tx, long cuentaId)
    {
        var total = await cn.ExecuteScalarAsync<long>(
            "SELECT COALESCE(SUM(precio * cantidad), 0) FROM linea WHERE cuenta_id = @c AND anulada = 0", new { c = cuentaId }, tx);
        var pagado = await cn.ExecuteScalarAsync<long>(
            "SELECT COALESCE(SUM(monto), 0) FROM pago WHERE cuenta_id = @c AND anulado = 0", new { c = cuentaId }, tx);
        return (total, pagado);
    }

    public static void MapCobro(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapPost("/cuentas/{id:long}/cobrar", async (long id, CobrarDto dto, Db db, Eventos ev, HttpContext ctx) =>
        {
            var modo = V.Opcion(dto.Modo, "La forma de cobro", "todo", "personas", "lineas", "monto");
            var metodo = V.Opcion(dto.Metodo, "El método de pago", "efectivo", "transferencia", "tarjeta");

            var pagoId = await db.Transaccion(async (cn, tx) =>
            {
                // Reintento del mismo cobro (por mala señal): no se cobra dos veces.
                if (!string.IsNullOrEmpty(dto.Uid))
                {
                    var previo = await cn.ExecuteScalarAsync<long?>("SELECT id FROM pago WHERE uid = @u", new { u = dto.Uid }, tx);
                    if (previo is not null) return previo.Value;
                }

                var estado = await cn.ExecuteScalarAsync<string?>("SELECT estado FROM cuenta WHERE id = @id", new { id }, tx)
                    ?? throw new NegocioException("La cuenta no existe.", 404);
                if (estado != "abierta") throw new NegocioException("Esta cuenta ya está cerrada.", 409);

                var (total, pagado) = await Saldos(cn, tx, id);
                var falta = total - pagado;
                if (falta <= 0) throw new NegocioException("Esta cuenta no tiene nada pendiente por cobrar.", 409);

                long monto;
                List<long> lineasIds = [];
                string etiqueta;

                switch (modo)
                {
                    case "todo":
                        monto = falta;
                        etiqueta = "Cuenta completa";
                        lineasIds = (await cn.QueryAsync<long>(
                            "SELECT id FROM linea WHERE cuenta_id = @c AND anulada = 0 AND pago_id IS NULL", new { c = id }, tx)).ToList();
                        break;

                    case "personas":
                    {
                        if (dto.Personas is not { Count: > 0 }) throw new NegocioException("Elige al menos una persona para cobrar.");
                        var numeros = dto.Personas.Distinct().ToList();
                        var cond = numeros.Contains(0) ? "(co.numero IN @n OR l.comensal_id IS NULL)" : "co.numero IN @n";
                        var filas = (await cn.QueryAsync(
                            $@"SELECT l.id, l.precio * l.cantidad AS sub FROM linea l LEFT JOIN comensal co ON co.id = l.comensal_id
                               WHERE l.cuenta_id = @c AND l.anulada = 0 AND l.pago_id IS NULL AND {cond}",
                            new { c = id, n = numeros.Where(x => x > 0).ToList() }, tx)).ToList();
                        if (filas.Count == 0) throw new NegocioException("Esas personas no tienen nada pendiente.");
                        lineasIds = filas.Select(f => (long)f.id).ToList();
                        monto = filas.Sum(f => (long)f.sub);
                        var nombres = numeros.Select(n => n == 0 ? "Común" : $"Persona {n}");
                        etiqueta = string.Join(", ", nombres);
                        break;
                    }

                    case "lineas":
                    {
                        if (dto.Lineas is not { Count: > 0 }) throw new NegocioException("Elige al menos un producto para cobrar.");
                        var ids = dto.Lineas.Distinct().ToList();
                        var filas = (await cn.QueryAsync(
                            @"SELECT id, precio * cantidad AS sub FROM linea
                              WHERE cuenta_id = @c AND anulada = 0 AND pago_id IS NULL AND id IN @ids", new { c = id, ids }, tx)).ToList();
                        if (filas.Count != ids.Count) throw new NegocioException("Algún producto ya fue pagado o no pertenece a la cuenta. Revisa la cuenta.", 409);
                        lineasIds = ids;
                        monto = filas.Sum(f => (long)f.sub);
                        etiqueta = $"{ids.Count} producto(s)";
                        break;
                    }

                    default: // monto libre
                        monto = V.Dinero(dto.Monto, "El monto", permitirCero: false);
                        etiqueta = V.TextoOpc(dto.Etiqueta, "La etiqueta", 40) ?? "Abono";
                        break;
                }

                if (monto > falta)
                    throw new NegocioException($"Ese cobro ({monto:N0}) es mayor que lo que falta ({falta:N0}). Revisa la cuenta.", 409);
                if (modo != "monto" && !string.IsNullOrWhiteSpace(dto.Etiqueta)) etiqueta = V.Texto(dto.Etiqueta, "La etiqueta", 40);

                long recibido = monto, cambio = 0;
                if (metodo == "efectivo" && dto.Recibido is not null)
                {
                    recibido = V.Dinero(dto.Recibido, "El efectivo recibido", permitirCero: false);
                    if (recibido < monto) throw new NegocioException($"El efectivo recibido ({recibido:N0}) es menor que el cobro ({monto:N0}).");
                    cambio = recibido - monto;
                }

                var pago = await cn.ExecuteScalarAsync<long>(
                    @"INSERT INTO pago (uid, cuenta_id, monto, metodo, recibido, cambio, etiqueta, cajero_id)
                      VALUES (@uid, @c, @monto, @metodo, @recibido, @cambio, @etiqueta, @cajero); SELECT last_insert_rowid();",
                    new
                    {
                        uid = string.IsNullOrEmpty(dto.Uid) ? null : dto.Uid, c = id, monto, metodo, recibido, cambio,
                        etiqueta, cajero = ctx.User.UsuarioId(),
                    }, tx);

                if (lineasIds.Count > 0)
                    await cn.ExecuteAsync("UPDATE linea SET pago_id = @p WHERE id IN @ids", new { p = pago, ids = lineasIds }, tx);

                // Si ya no falta nada, la cuenta se cierra y la mesa queda libre.
                if (falta - monto == 0)
                {
                    await cn.ExecuteAsync(
                        "UPDATE linea SET pago_id = @p WHERE cuenta_id = @c AND anulada = 0 AND pago_id IS NULL", new { p = pago, c = id }, tx);
                    await cn.ExecuteAsync(
                        "UPDATE cuenta SET estado = 'pagada', cerrada_en = @ahora, por_cobrar = 0 WHERE id = @c", new { ahora = Db.Ahora(), c = id }, tx);
                }
                return pago;
            });

            ev.Emitir("cuenta", new { id });
            ev.Emitir("mesas");
            await using var cn2 = await db.Abrir();
            var detalle = await CuentasEndpoints.Cargar(cn2, id);
            return Results.Ok(new { pagoId, detalle });
        }).Solo(Roles.Admin, Roles.Cajero);

        // Solo el administrador deshace un cobro (queda en la auditoría).
        g.MapPost("/pagos/{id:long}/anular", async (long id, MotivoDto dto, Db db, Eventos ev, HttpContext ctx) =>
        {
            var motivo = V.Texto(dto.Motivo, "El motivo", 200);
            var cuentaId = await db.Transaccion(async (cn, tx) =>
            {
                var p = await cn.QuerySingleOrDefaultAsync("SELECT cuenta_id AS cuentaId, anulado, monto FROM pago WHERE id = @id", new { id }, tx)
                    ?? throw new NegocioException("Ese cobro no existe.", 404);
                if (p.anulado != 0) throw new NegocioException("Ese cobro ya estaba anulado.", 409);
                long cuenta = p.cuentaId;

                var c = await cn.QuerySingleAsync("SELECT estado, mesa_id AS mesaId FROM cuenta WHERE id = @c", new { c = cuenta }, tx);
                if (c.estado == "anulada") throw new NegocioException("La cuenta está anulada.", 409);
                if (c.estado == "pagada" && c.mesaId is not null &&
                    await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM cuenta WHERE mesa_id = @m AND estado = 'abierta'", new { m = (long)c.mesaId }, tx) > 0)
                    throw new NegocioException("Esa mesa ya fue ocupada por otra cuenta; no se puede reabrir esta.", 409);

                await cn.ExecuteAsync(
                    "UPDATE pago SET anulado = 1, anulado_por = @u, motivo_anulacion = @m WHERE id = @id",
                    new { u = ctx.User.UsuarioId(), m = motivo, id }, tx);
                await cn.ExecuteAsync("UPDATE linea SET pago_id = NULL WHERE pago_id = @id", new { id }, tx);
                if (c.estado == "pagada")
                    await cn.ExecuteAsync("UPDATE cuenta SET estado = 'abierta', cerrada_en = NULL WHERE id = @c", new { c = cuenta }, tx);
                await cn.Auditar(ctx.User.UsuarioId(), "pago_anulado", $"pago {id} de {(long)p.monto:N0} (cuenta {cuenta}): {motivo}", tx);
                return cuenta;
            });
            ev.Emitir("cuenta", new { id = cuentaId });
            ev.Emitir("mesas");
            await using var cn2 = await db.Abrir();
            return Results.Ok(await CuentasEndpoints.Cargar(cn2, cuentaId));
        }).Solo(Roles.Admin);

        // Encuesta rápida de 2 preguntas al pagar. Es opcional y no bloquea nada.
        g.MapPost("/cuentas/{id:long}/encuesta", async (long id, EncuestaDto dto, Db db) =>
        {
            int? Estrellas(int? v, string campo) => v is null ? null : V.Entero(v, campo, 1, 5);
            var atencion = Estrellas(dto.Atencion, "La calificación de atención");
            var comida = Estrellas(dto.Comida, "La calificación de la comida");
            var comentario = V.TextoOpc(dto.Comentario, "El comentario", 300);
            if (atencion is null && comida is null && comentario is null) throw new NegocioException("La encuesta está vacía.");

            await db.Transaccion(async (cn, tx) =>
            {
                var meseroId = await cn.ExecuteScalarAsync<long?>("SELECT mesero_id FROM cuenta WHERE id = @id", new { id }, tx)
                    ?? throw new NegocioException("La cuenta no existe.", 404);
                if (await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM encuesta WHERE cuenta_id = @id", new { id }, tx) > 0)
                    throw new NegocioException("Esta cuenta ya tiene encuesta.", 409);
                await cn.ExecuteAsync(
                    "INSERT INTO encuesta (cuenta_id, mesero_id, atencion, comida, comentario) VALUES (@id, @m, @a, @c, @t)",
                    new { id, m = meseroId, a = atencion, c = comida, t = comentario }, tx);
                return 0;
            });
            return Results.Ok(new { mensaje = "¡Gracias por la opinión!" });
        });
    }
}
