using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Dapper;
using Microsoft.Data.Sqlite;
using DulceTentacion.Infra;

namespace DulceTentacion.Endpoints;

public static class AdminEndpoints
{
    private record VarianteDto(string? Nombre, long? Precio);
    private record GrupoDto(string? Nombre, int? Min, int? Max, List<string>? Opciones);
    private record ProductoDto(int? CategoriaId, string? Seccion, string? Nombre, long? Precio, int? Orden, bool? Activo,
                               List<VarianteDto>? Variantes, List<GrupoDto>? Grupos);
    private record ActivoDto(bool? Activo);
    private record PrecioDto(long? Precio);
    private record CategoriaDto(string? Nombre, string? Icono, int? Orden, bool? Activa);
    private record MesaDto(string? Nombre, int? Orden, bool? Activa);
    private record UsuarioDto(string? Nombre, string? Rol, string? Pin, bool? Activo, int? Orden);
    private record PinDto(string? Pin);
    private record AjusteDto(int? CategoriaId, decimal? Porcentaje, int? Redondeo);

    public static void MapAdmin(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization().Solo(Roles.Admin);

        // ------------------------------------------------------------ Menú
        g.MapGet("/menu", async (Db db) => Results.Ok(await MenuEndpoints.CargarMenu(db, incluirInactivos: true)));

        g.MapPost("/productos", async (ProductoDto dto, Db db, Eventos ev, HttpContext ctx) =>
        {
            var id = await db.Transaccion(async (cn, tx) =>
            {
                var datos = await ValidarProducto(cn, tx, dto);
                var nuevo = await cn.ExecuteScalarAsync<long>(
                    @"INSERT INTO producto (categoria_id, seccion, nombre, precio, activo, orden)
                      VALUES (@cat, @sec, @nom, @precio, @act, @orden); SELECT last_insert_rowid();", datos, tx);
                await GuardarHijos(cn, tx, nuevo, dto);
                await MenuEndpoints.Tocar(cn, tx);
                await cn.Auditar(ctx.User.UsuarioId(), "producto_creado", $"{datos.nom} ({datos.precio:N0})", tx);
                return nuevo;
            });
            ev.Emitir("menu");
            return Results.Ok(new { id });
        });

        g.MapPut("/productos/{id:long}", async (long id, ProductoDto dto, Db db, Eventos ev, HttpContext ctx) =>
        {
            await db.Transaccion(async (cn, tx) =>
            {
                var antes = await cn.QuerySingleOrDefaultAsync("SELECT nombre, precio FROM producto WHERE id = @id", new { id }, tx)
                    ?? throw new NegocioException("Ese producto no existe.", 404);
                var d = await ValidarProducto(cn, tx, dto);
                await cn.ExecuteAsync(
                    @"UPDATE producto SET categoria_id = @cat, seccion = @sec, nombre = @nom, precio = @precio, activo = @act, orden = @orden
                      WHERE id = @id", new { d.cat, d.sec, d.nom, d.precio, d.act, d.orden, id }, tx);
                await cn.ExecuteAsync("DELETE FROM variante WHERE producto_id = @id", new { id }, tx);
                await cn.ExecuteAsync("DELETE FROM grupo_opcion WHERE producto_id = @id", new { id }, tx);
                await GuardarHijos(cn, tx, id, dto);
                await MenuEndpoints.Tocar(cn, tx);
                if ((long)antes.precio != d.precio)
                    await cn.Auditar(ctx.User.UsuarioId(), "precio_cambiado", $"{d.nom}: {(long)antes.precio:N0} → {d.precio:N0}", tx);
                return 0;
            });
            ev.Emitir("menu");
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/productos/{id:long}/activo", async (long id, ActivoDto dto, Db db, Eventos ev) =>
        {
            await using var cn = await db.Abrir();
            var filas = await cn.ExecuteAsync("UPDATE producto SET activo = @a WHERE id = @id", new { a = dto.Activo == true ? 1 : 0, id });
            if (filas == 0) throw new NegocioException("Ese producto no existe.", 404);
            await MenuEndpoints.Tocar(cn);
            ev.Emitir("menu");
            return Results.Ok(new { ok = true });
        });

        // Cambio rápido de precio desde la lista (productos sin presentaciones).
        g.MapPut("/productos/{id:long}/precio", async (long id, PrecioDto dto, Db db, Eventos ev, HttpContext ctx) =>
        {
            var precio = V.Dinero(dto.Precio, "El precio", permitirCero: false);
            await db.Transaccion(async (cn, tx) =>
            {
                var p = await cn.QuerySingleOrDefaultAsync("SELECT nombre, precio FROM producto WHERE id = @id", new { id }, tx)
                    ?? throw new NegocioException("Ese producto no existe.", 404);
                if (await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM variante WHERE producto_id = @id", new { id }, tx) > 0)
                    throw new NegocioException("Este producto tiene presentaciones: edítalo para cambiar cada precio.");
                await cn.ExecuteAsync("UPDATE producto SET precio = @precio WHERE id = @id", new { precio, id }, tx);
                await MenuEndpoints.Tocar(cn, tx);
                await cn.Auditar(ctx.User.UsuarioId(), "precio_cambiado", $"{p.nombre}: {(long)p.precio:N0} → {precio:N0}", tx);
                return 0;
            });
            ev.Emitir("menu");
            return Results.Ok(new { ok = true });
        });

        g.MapDelete("/productos/{id:long}", async (long id, Db db, Eventos ev) =>
        {
            var desactivado = await db.Transaccion(async (cn, tx) =>
            {
                if (await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM producto WHERE id = @id", new { id }, tx) == 0)
                    throw new NegocioException("Ese producto no existe.", 404);
                var usado = await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM linea WHERE producto_id = @id", new { id }, tx) > 0;
                // Si ya se vendió, se oculta (para no perder las estadísticas); si nunca se usó, se borra.
                if (usado) await cn.ExecuteAsync("UPDATE producto SET activo = 0 WHERE id = @id", new { id }, tx);
                else await cn.ExecuteAsync("DELETE FROM producto WHERE id = @id", new { id }, tx);
                await MenuEndpoints.Tocar(cn, tx);
                return usado;
            });
            ev.Emitir("menu");
            return Results.Ok(new { ok = true, desactivado });
        });

        // ------------------------------------------------------------ Categorías
        g.MapPost("/categorias", async (CategoriaDto dto, Db db, Eventos ev) =>
        {
            var nombre = V.Texto(dto.Nombre, "El nombre", 40);
            await using var cn = await db.Abrir();
            var orden = dto.Orden ?? (int)await cn.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(orden), 0) + 1 FROM categoria");
            var id = await cn.ExecuteScalarAsync<long>(
                "INSERT INTO categoria (nombre, icono, orden) VALUES (@n, @i, @o); SELECT last_insert_rowid();",
                new { n = nombre, i = V.TextoOpc(dto.Icono, "El icono", 8) ?? "🍽️", o = orden });
            await MenuEndpoints.Tocar(cn);
            ev.Emitir("menu");
            return Results.Ok(new { id });
        });

        g.MapPut("/categorias/{id:int}", async (int id, CategoriaDto dto, Db db, Eventos ev) =>
        {
            var nombre = V.Texto(dto.Nombre, "El nombre", 40);
            await using var cn = await db.Abrir();
            var filas = await cn.ExecuteAsync(
                @"UPDATE categoria SET nombre = @n, icono = COALESCE(@i, icono), orden = COALESCE(@o, orden), activa = COALESCE(@a, activa) WHERE id = @id",
                new { n = nombre, i = V.TextoOpc(dto.Icono, "El icono", 8), o = dto.Orden, a = dto.Activa is null ? (int?)null : dto.Activa == true ? 1 : 0, id });
            if (filas == 0) throw new NegocioException("Esa categoría no existe.", 404);
            await MenuEndpoints.Tocar(cn);
            ev.Emitir("menu");
            return Results.Ok(new { ok = true });
        });

        g.MapDelete("/categorias/{id:int}", async (int id, Db db, Eventos ev) =>
        {
            await using var cn = await db.Abrir();
            if (await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM producto WHERE categoria_id = @id", new { id }) > 0)
                throw new NegocioException("Esa categoría todavía tiene productos. Muévelos o desactívala.", 409);
            await cn.ExecuteAsync("DELETE FROM categoria WHERE id = @id", new { id });
            await MenuEndpoints.Tocar(cn);
            ev.Emitir("menu");
            return Results.Ok(new { ok = true });
        });

        // ------------------------------------------------------------ Mesas
        g.MapGet("/mesas", async (Db db) =>
        {
            await using var cn = await db.Abrir();
            return Results.Ok(await cn.QueryAsync("SELECT id, nombre, orden, activa FROM mesa ORDER BY orden, id"));
        });

        g.MapPost("/mesas", async (MesaDto dto, Db db, Eventos ev) =>
        {
            var nombre = V.Texto(dto.Nombre, "El nombre de la mesa", 30);
            await using var cn = await db.Abrir();
            var orden = dto.Orden ?? (int)await cn.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(orden), 0) + 1 FROM mesa");
            var id = await cn.ExecuteScalarAsync<long>("INSERT INTO mesa (nombre, orden) VALUES (@n, @o); SELECT last_insert_rowid();", new { n = nombre, o = orden });
            ev.Emitir("mesas");
            return Results.Ok(new { id });
        });

        g.MapPut("/mesas/{id:int}", async (int id, MesaDto dto, Db db, Eventos ev) =>
        {
            var nombre = V.Texto(dto.Nombre, "El nombre de la mesa", 30);
            await using var cn = await db.Abrir();
            if (dto.Activa == false &&
                await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM cuenta WHERE mesa_id = @id AND estado = 'abierta'", new { id }) > 0)
                throw new NegocioException("Esa mesa tiene una cuenta abierta; ciérrala antes de desactivarla.", 409);
            var filas = await cn.ExecuteAsync(
                "UPDATE mesa SET nombre = @n, orden = COALESCE(@o, orden), activa = COALESCE(@a, activa) WHERE id = @id",
                new { n = nombre, o = dto.Orden, a = dto.Activa is null ? (int?)null : dto.Activa == true ? 1 : 0, id });
            if (filas == 0) throw new NegocioException("Esa mesa no existe.", 404);
            ev.Emitir("mesas");
            return Results.Ok(new { ok = true });
        });

        // ------------------------------------------------------------ Personal
        g.MapGet("/usuarios", async (Db db) =>
        {
            await using var cn = await db.Abrir();
            return Results.Ok(await cn.QueryAsync("SELECT id, nombre, rol, activo, orden FROM usuario ORDER BY activo DESC, orden, nombre"));
        });

        g.MapPost("/usuarios", async (UsuarioDto dto, Db db, HttpContext ctx) =>
        {
            var nombre = V.Texto(dto.Nombre, "El nombre", 60);
            var rol = V.Opcion(dto.Rol, "El rol", Roles.Todos);
            var pin = V.Pin(dto.Pin);
            await using var cn = await db.Abrir();
            if (await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM usuario WHERE lower(nombre) = lower(@n)", new { n = nombre }) > 0)
                throw new NegocioException("Ya existe una persona con ese nombre.", 409);
            var orden = await cn.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(orden), 0) + 1 FROM usuario");
            var id = await cn.ExecuteScalarAsync<long>(
                "INSERT INTO usuario (nombre, rol, pin_hash, orden) VALUES (@n, @r, @h, @o); SELECT last_insert_rowid();",
                new { n = nombre, r = rol, h = Seguridad.HashPin(pin), o = orden });
            await cn.Auditar(ctx.User.UsuarioId(), "usuario_creado", $"{nombre} ({rol})");
            return Results.Ok(new { id });
        });

        g.MapPut("/usuarios/{id:int}", async (int id, UsuarioDto dto, Db db, HttpContext ctx) =>
        {
            var nombre = V.Texto(dto.Nombre, "El nombre", 60);
            var rol = V.Opcion(dto.Rol, "El rol", Roles.Todos);
            var activo = dto.Activo ?? true;
            await db.Transaccion(async (cn, tx) =>
            {
                var actual = await cn.QuerySingleOrDefaultAsync("SELECT rol, activo FROM usuario WHERE id = @id", new { id }, tx)
                    ?? throw new NegocioException("Esa persona no existe.", 404);
                if (await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM usuario WHERE lower(nombre) = lower(@n) AND id <> @id", new { n = nombre, id }, tx) > 0)
                    throw new NegocioException("Ya existe una persona con ese nombre.", 409);
                var dejaDeSerAdmin = actual.rol == Roles.Admin && actual.activo == 1 && (rol != Roles.Admin || !activo);
                if (dejaDeSerAdmin)
                {
                    var otros = await cn.ExecuteScalarAsync<long>(
                        "SELECT COUNT(*) FROM usuario WHERE rol = 'administrador' AND activo = 1 AND id <> @id", new { id }, tx);
                    if (otros == 0) throw new NegocioException("Debe quedar al menos un administrador activo.", 409);
                }
                await cn.ExecuteAsync(
                    "UPDATE usuario SET nombre = @n, rol = @r, activo = @a, orden = COALESCE(@o, orden) WHERE id = @id",
                    new { n = nombre, r = rol, a = activo ? 1 : 0, o = dto.Orden, id }, tx);
                await cn.Auditar(ctx.User.UsuarioId(), "usuario_editado", $"{nombre} ({rol}, {(activo ? "activo" : "inactivo")})", tx);
                return 0;
            });
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/usuarios/{id:int}/pin", async (int id, PinDto dto, Db db, HttpContext ctx) =>
        {
            var pin = V.Pin(dto.Pin);
            await using var cn = await db.Abrir();
            var filas = await cn.ExecuteAsync(
                "UPDATE usuario SET pin_hash = @h, intentos_fallidos = 0, bloqueado_hasta = NULL WHERE id = @id",
                new { h = Seguridad.HashPin(pin), id });
            if (filas == 0) throw new NegocioException("Esa persona no existe.", 404);
            await cn.Auditar(ctx.User.UsuarioId(), "pin_restablecido", $"usuario {id}");
            return Results.Ok(new { ok = true });
        });

        // ------------------------------------------------------------ Solicitudes de cambio de PIN
        g.MapGet("/solicitudes-pin", async (Db db) =>
        {
            await using var cn = await db.Abrir();
            return Results.Ok(await cn.QueryAsync(
                @"SELECT s.id, s.usuario_id AS usuarioId, u.nombre, u.rol, s.creada_en AS creadaEn
                  FROM solicitud_pin s JOIN usuario u ON u.id = s.usuario_id WHERE s.estado = 'pendiente' ORDER BY s.id"));
        });

        g.MapPost("/solicitudes-pin/{id:long}/{decision}", async (long id, string decision, Db db, Eventos ev, HttpContext ctx) =>
        {
            if (decision is not ("aprobar" or "rechazar")) throw new NegocioException("Decisión no válida.", 404);
            var nombre = await db.Transaccion(async (cn, tx) =>
            {
                var s = await cn.QuerySingleOrDefaultAsync(
                    @"SELECT s.usuario_id AS usuarioId, s.pin_hash AS pinHash, s.estado, u.nombre
                      FROM solicitud_pin s JOIN usuario u ON u.id = s.usuario_id WHERE s.id = @id", new { id }, tx)
                    ?? throw new NegocioException("Esa solicitud no existe.", 404);
                if (s.estado != "pendiente") throw new NegocioException("Esa solicitud ya fue resuelta.", 409);
                if (decision == "aprobar")
                    await cn.ExecuteAsync(
                        "UPDATE usuario SET pin_hash = @h, intentos_fallidos = 0, bloqueado_hasta = NULL WHERE id = @u",
                        new { h = (string)s.pinHash, u = (long)s.usuarioId }, tx);
                await cn.ExecuteAsync(
                    "UPDATE solicitud_pin SET estado = @e, resuelta_por = @p, resuelta_en = @ahora WHERE id = @id",
                    new { e = decision == "aprobar" ? "aprobada" : "rechazada", p = ctx.User.UsuarioId(), ahora = Db.Ahora(), id }, tx);
                await cn.Auditar(ctx.User.UsuarioId(), decision == "aprobar" ? "pin_aprobado" : "pin_rechazado", (string)s.nombre, tx);
                return (string)s.nombre;
            });
            ev.Emitir("solicitud_pin");
            return Results.Ok(new { mensaje = decision == "aprobar" ? $"PIN de {nombre} actualizado." : $"Solicitud de {nombre} rechazada." });
        });

        // ------------------------------------------------------------ Ajuste de precios por porcentaje
        g.MapPost("/precios/vista", async (AjusteDto dto, Db db) =>
        {
            await using var cn = await db.Abrir();
            return Results.Ok((await CalcularAjuste(cn, null, dto)).Vista);
        });

        g.MapPost("/precios/aplicar", async (AjusteDto dto, Db db, Eventos ev, HttpContext ctx) =>
        {
            var cambios = await db.Transaccion(async (cn, tx) =>
            {
                var r = await CalcularAjuste(cn, tx, dto);
                foreach (var c in r.Productos)
                    await cn.ExecuteAsync("UPDATE producto SET precio = @n WHERE id = @id", new { n = c.Nuevo, id = c.Id }, tx);
                foreach (var v in r.Variantes)
                    await cn.ExecuteAsync("UPDATE variante SET precio = @n WHERE id = @id", new { n = v.Nuevo, id = v.Id }, tx);
                // el precio base de un producto con presentaciones es el de la más barata
                await cn.ExecuteAsync(
                    @"UPDATE producto SET precio = (SELECT MIN(v.precio) FROM variante v WHERE v.producto_id = producto.id)
                      WHERE EXISTS (SELECT 1 FROM variante v WHERE v.producto_id = producto.id)", transaction: tx);
                await MenuEndpoints.Tocar(cn, tx);
                await cn.Auditar(ctx.User.UsuarioId(), "ajuste_precios",
                    $"{dto.Porcentaje}% en {(dto.CategoriaId is null ? "todo el menú" : $"categoría {dto.CategoriaId}")} ({r.Productos.Count + r.Variantes.Count} precios)", tx);
                return r.Productos.Count + r.Variantes.Count;
            });
            ev.Emitir("menu");
            return Results.Ok(new { cambios });
        });

        // ------------------------------------------------------------ Sistema
        g.MapGet("/sistema", (Db db, Respaldo respaldo, IConfiguration cfg) =>
        {
            var ips = IpsLocales();
            var tam = File.Exists(db.Ruta) ? new FileInfo(db.Ruta).Length : 0;
            return Results.Ok(new
            {
                ips,
                puerto = new Uri(cfg["urls"]?.Split(';')[0].Replace("+", "localhost").Replace("0.0.0.0", "localhost") ?? "http://localhost:8080").Port,
                baseDeDatos = new { ruta = db.Ruta, bytes = tam },
                respaldos = respaldo.Existentes().Take(30).Select(f => new { nombre = f.Name, bytes = f.Length, fecha = f.CreationTime.ToString("yyyy-MM-dd HH:mm") }),
            });
        });

        g.MapPost("/respaldos", async (Respaldo respaldo, HttpContext ctx, Db db) =>
        {
            var ruta = await respaldo.Crear();
            await using var cn = await db.Abrir();
            await cn.Auditar(ctx.User.UsuarioId(), "respaldo_manual", Path.GetFileName(ruta));
            return Results.Ok(new { nombre = Path.GetFileName(ruta) });
        });

        g.MapGet("/respaldos/{nombre}", (string nombre, Respaldo respaldo) =>
        {
            var archivo = respaldo.Existentes().FirstOrDefault(f => f.Name == nombre);   // solo nombres que realmente existen
            return archivo is null
                ? Results.NotFound(new { mensaje = "Ese respaldo no existe." })
                : Results.File(archivo.FullName, "application/octet-stream", archivo.Name);
        });
    }

    // ---------------------------------------------------------------- Apoyos
    // Solo adaptadores reales (WiFi / cable) con salida a la red; se ignoran los virtuales (Docker, WSL, VPN, VirtualBox…).
    private static List<string> IpsLocales()
    {
        static bool Virtual(NetworkInterface n) =>
            System.Text.RegularExpressions.Regex.IsMatch(n.Name + " " + n.Description, "vEthernet|Virtual|VMware|Hyper-V|WSL|Docker|TAP|TUN|VPN|Loopback|Bluetooth", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var todas = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && !Virtual(n))
            .ToList();
        var conSalida = todas.Where(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))).ToList();
        return (conSalida.Count > 0 ? conSalida : todas)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254"))
            .Select(a => a.Address.ToString()).Distinct().ToList();
    }

    private record Prod(int cat, string? sec, string nom, long precio, int act, int orden);

    private static async Task<Prod> ValidarProducto(SqliteConnection cn, SqliteTransaction tx, ProductoDto dto)
    {
        var nombre = V.Texto(dto.Nombre, "El nombre del producto", 80);
        var cat = V.Entero(dto.CategoriaId, "La categoría", 1);
        if (await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM categoria WHERE id = @cat", new { cat }, tx) == 0)
            throw new NegocioException("Esa categoría no existe.");

        var variantes = dto.Variantes ?? [];
        long precio;
        if (variantes.Count > 0)
        {
            if (variantes.Count > 40) throw new NegocioException("Demasiadas presentaciones (máximo 40).");
            precio = variantes.Min(v => (long)V.Dinero(v.Precio, "El precio de cada presentación", permitirCero: false));
        }
        else precio = V.Dinero(dto.Precio, "El precio", permitirCero: false);

        var orden = dto.Orden ?? (int)await cn.ExecuteScalarAsync<long>(
            "SELECT COALESCE(MAX(orden), 0) + 1 FROM producto WHERE categoria_id = @cat", new { cat }, tx);
        return new Prod(cat, V.TextoOpc(dto.Seccion, "La sección", 40), nombre, precio, dto.Activo == false ? 0 : 1, orden);
    }

    private static async Task GuardarHijos(SqliteConnection cn, SqliteTransaction tx, long productoId, ProductoDto dto)
    {
        var orden = 0;
        foreach (var v in dto.Variantes ?? [])
            await cn.ExecuteAsync("INSERT INTO variante (producto_id, nombre, precio, orden) VALUES (@p, @n, @pr, @o)",
                new { p = productoId, n = V.Texto(v.Nombre, "El nombre de la presentación", 40), pr = V.Dinero(v.Precio, "El precio", false), o = orden++ }, tx);

        orden = 0;
        foreach (var gr in dto.Grupos ?? [])
        {
            var ops = (gr.Opciones ?? []).Select(o => (o ?? "").Trim()).Where(o => o.Length > 0).ToList();
            if (ops.Count == 0) throw new NegocioException("Cada grupo de opciones necesita al menos una opción.");
            var min = Math.Clamp(gr.Min ?? 1, 0, ops.Count);
            var max = Math.Clamp(gr.Max ?? 1, Math.Max(min, 1), ops.Count);
            var grupoId = await cn.ExecuteScalarAsync<long>(
                "INSERT INTO grupo_opcion (producto_id, nombre, min_sel, max_sel, orden) VALUES (@p, @n, @mi, @ma, @o); SELECT last_insert_rowid();",
                new { p = productoId, n = V.Texto(gr.Nombre, "El nombre del grupo de opciones", 40), mi = min, ma = max, o = orden++ }, tx);
            var oo = 0;
            foreach (var op in ops)
            {
                var (nombreOp, extra) = V.OpcionConExtra(op);
                await cn.ExecuteAsync("INSERT INTO opcion (grupo_id, nombre, precio_extra, orden) VALUES (@g, @n, @x, @o)",
                    new { g = grupoId, n = V.Texto(nombreOp, "La opción", 40), x = extra, o = oo++ }, tx);
            }
        }
    }

    private record Cambio(long Id, string Nombre, long Antes, long Nuevo);
    private record ResultadoAjuste(List<Cambio> Productos, List<Cambio> Variantes)
    {
        public object Vista => new { total = Productos.Count + Variantes.Count, ejemplos = Productos.Concat(Variantes).Take(15) };
    }

    private static long Redondear(decimal v, int paso) => paso <= 1 ? (long)Math.Round(v) : (long)(Math.Round(v / paso, MidpointRounding.AwayFromZero) * paso);

    private static async Task<ResultadoAjuste> CalcularAjuste(SqliteConnection cn, SqliteTransaction? tx, AjusteDto dto)
    {
        var pct = dto.Porcentaje ?? throw new NegocioException("Escribe el porcentaje.");
        if (pct == 0 || pct < -50 || pct > 100) throw new NegocioException("El porcentaje debe estar entre -50 y 100, y no puede ser 0.");
        var paso = dto.Redondeo is 0 or 50 or 100 or 500 or 1000 ? dto.Redondeo!.Value : 100;
        var factor = 1 + pct / 100m;
        var filtro = dto.CategoriaId is null ? "" : "AND p.categoria_id = @cat";

        var productos = (await cn.QueryAsync(
            $@"SELECT p.id, p.nombre, p.precio FROM producto p
               WHERE p.activo = 1 {filtro} AND NOT EXISTS (SELECT 1 FROM variante v WHERE v.producto_id = p.id)",
            new { cat = dto.CategoriaId }, tx))
            .Select(p => new Cambio(p.id, p.nombre, p.precio, Redondear((long)p.precio * factor, paso))).ToList();
        var variantes = (await cn.QueryAsync(
            $@"SELECT v.id, p.nombre || ' · ' || v.nombre AS nombre, v.precio FROM variante v JOIN producto p ON p.id = v.producto_id
               WHERE p.activo = 1 {filtro}", new { cat = dto.CategoriaId }, tx))
            .Select(v => new Cambio(v.id, v.nombre, v.precio, Redondear((long)v.precio * factor, paso))).ToList();

        // el precio base de un producto con presentaciones es el menor de ellas: se recalcula después de aplicar
        return new ResultadoAjuste(productos.Where(c => c.Nuevo > 0).ToList(), variantes.Where(c => c.Nuevo > 0).ToList());
    }
}
