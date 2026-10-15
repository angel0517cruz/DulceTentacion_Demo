using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace DulceTentacion.Infra;

/// <summary>
/// Datos iniciales de la primera vez: el menú completo de la cafetería, las personas del equipo y las mesas.
/// Solo se cargan si las tablas están vacías, así que reiniciar el sistema nunca duplica nada.
/// </summary>
public static class Semilla
{
    private sealed record Var(string Nombre, int Precio);
    private sealed record Grupo(string Nombre, int Min, int Max, string[] Opciones);
    private sealed record Prod(string Categoria, string? Seccion, string Nombre, int Precio, Var[]? Variantes = null, Grupo[]? Grupos = null);

    public static async Task Cargar(Db db, ILogger log, bool demo = false)
    {
        await using var cn = await db.Abrir();
        if (await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM categoria") == 0) await CargarMenu(db);
        if (await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM usuario") == 0) await CargarPersonal(db, log, demo);
        if (await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM mesa") == 0)
            for (var i = 1; i <= 12; i++)
                await cn.ExecuteAsync("INSERT INTO mesa (nombre, orden) VALUES (@n, @o)", new { n = $"Mesa {i}", o = i });
        if (demo && await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cuenta") == 0) await SemillaDemo.Cargar(db, log);
    }

    // ------------------------------------------------------------------ Personal
    private static async Task CargarPersonal(Db db, ILogger log, bool demo)
    {
        var equipo = new (string Nombre, string Rol)[]
        {
            ("Norberto Cruz", Roles.Admin), ("Lina Londoño", Roles.Admin), ("Miguel Cruz", Roles.Admin),
            ("Liliana Alvarado", Roles.Mesero), ("Mariana Melo", Roles.Mesero), ("Xiomara Garzón", Roles.Mesero),
            ("Nayeli Garzón", Roles.Mesero), ("Angie Cifuentes", Roles.Mesero), ("Alejandra", Roles.Mesero),
        };
        var sb = new StringBuilder();
        sb.AppendLine("PINs INICIALES · Dulce Tentación");
        sb.AppendLine("Meseras: PIN 0000 (pueden cambiarlo en 'Mi cuenta'). Administradores: PIN propio; cámbialo en 'Mi cuenta' y borra este archivo.");
        sb.AppendLine();

        await using var cn = await db.Abrir();
        var orden = 0;
        foreach (var (nombre, rol) in equipo)
        {
            // Meseras: PIN 0000 (fácil de recordar). Administración: PIN aleatorio de 6 números.
            var pin = demo
                ? "1234"   // demostración: PIN conocido para todos
                : rol == Roles.Admin
                    ? string.Concat(Enumerable.Range(0, 6).Select(_ => RandomNumberGenerator.GetInt32(0, 10)))
                    : "0000";
            await cn.ExecuteAsync("INSERT INTO usuario (nombre, rol, pin_hash, orden) VALUES (@nombre, @rol, @hash, @orden)",
                new { nombre, rol, hash = Seguridad.HashPin(pin), orden = ++orden });
            sb.AppendLine($"{nombre,-20} {rol,-14} PIN: {pin}");
        }
        var archivo = Path.Combine(db.Carpeta, "PINS_INICIALES.txt");
        await File.WriteAllTextAsync(archivo, sb.ToString(), new UTF8Encoding(true));
        log.LogWarning("Se crearon {N} usuarios. Sus PIN iniciales están en: {Archivo}", equipo.Length, archivo);
    }

    // ------------------------------------------------------------------ Menú
    private static async Task CargarMenu(Db db)
    {
        var iconos = new Dictionary<string, string>
        {
            ["Desayunos"] = "🍳", ["Amasijos y Pastelería"] = "🥐", ["Bebidas Calientes"] = "☕", ["Bebidas Frías"] = "🥤",
            ["Sándwich y Génovas"] = "🥪", ["Quesos"] = "🧀", ["Papas y Pasabocas"] = "🍟", ["Dulcería"] = "🍬",
            ["Miel y Propóleo"] = "🍯", ["Licores"] = "🍷", ["Botiquín"] = "💊",
        };
        await using var cn = await db.Abrir();
        await using var tx = cn.BeginTransaction();
        var categorias = new Dictionary<string, long>();
        var o = 0;
        foreach (var (nombre, icono) in iconos)
            categorias[nombre] = await cn.ExecuteScalarAsync<long>(
                "INSERT INTO categoria (nombre, icono, orden) VALUES (@nombre, @icono, @orden); SELECT last_insert_rowid();",
                new { nombre, icono, orden = ++o }, tx);

        var ordenProd = 0;
        foreach (var p in Productos())
        {
            var precio = p.Variantes is { Length: > 0 } ? p.Variantes.Min(v => v.Precio) : p.Precio;
            var id = await cn.ExecuteScalarAsync<long>(
                @"INSERT INTO producto (categoria_id, seccion, nombre, precio, orden) VALUES (@c, @s, @n, @p, @o);
                  SELECT last_insert_rowid();",
                new { c = categorias[p.Categoria], s = p.Seccion, n = p.Nombre, p = precio, o = ++ordenProd }, tx);
            var k = 0;
            foreach (var v in p.Variantes ?? [])
                await cn.ExecuteAsync("INSERT INTO variante (producto_id, nombre, precio, orden) VALUES (@id, @n, @p, @o)",
                    new { id, n = v.Nombre, p = v.Precio, o = ++k }, tx);
            var g = 0;
            foreach (var grupo in p.Grupos ?? [])
            {
                var gid = await cn.ExecuteScalarAsync<long>(
                    @"INSERT INTO grupo_opcion (producto_id, nombre, min_sel, max_sel, orden) VALUES (@id, @n, @min, @max, @o);
                      SELECT last_insert_rowid();",
                    new { id, n = grupo.Nombre, min = grupo.Min, max = grupo.Max, o = ++g }, tx);
                var j = 0;
                foreach (var op in grupo.Opciones)
                {
                    var (nombreOp, extra) = V.OpcionConExtra(op);
                    await cn.ExecuteAsync("INSERT INTO opcion (grupo_id, nombre, precio_extra, orden) VALUES (@gid, @n, @x, @o)",
                        new { gid, n = nombreOp, x = extra, o = ++j }, tx);
                }
            }
        }
        await tx.CommitAsync();
    }

    private static IEnumerable<Prod> Productos()
    {
        var huevos = new Grupo("Huevos", 1, 1, ["Pericos", "Fritos", "Revueltos", "Rancheros +2000"]);
        var arroz = new Grupo("Arroz", 1, 1, ["Con arroz", "Sin arroz"]);
        var bebidaDesayuno = new Grupo("Bebida", 1, 1, ["Chocolate", "Tinto", "Café", "Aromática", "Jugo de naranja", "Aguapanela", "Gaseosa mini"]);
        var acompanante = new Grupo("Acompañante", 1, 1, ["Almojábana", "Pandebono"]);
        var carne = new Grupo("Carne", 1, 1, ["Pernil", "Cordero", "Pavo"]);
        Var[] sinOCon(int sin, int con) => [new("Sin arroz", sin), new("Con arroz", con)];

        // ---- Desayunos
        const string D = "Desayunos";
        yield return new(D, "Caldos", "Caldo de costilla", 9000);
        yield return new(D, "Caldos", "Caldo de pata", 14000);
        yield return new(D, "Caldos", "Caldo de pescado", 10000);
        yield return new(D, "Huevos", "Huevos pericos", 5000, sinOCon(5000, 7000));
        yield return new(D, "Huevos", "Huevos revueltos", 5000, sinOCon(5000, 7000));
        yield return new(D, "Huevos", "Huevos fritos", 5000, sinOCon(5000, 7000));
        yield return new(D, "Huevos", "Huevos rancheros", 9000, sinOCon(9000, 10000));
        yield return new(D, "Huevos", "Huevos a la cruz", 12000, sinOCon(12000, 14000));
        yield return new(D, "Platos y especiales", "Tamal", 10000);
        yield return new(D, "Platos y especiales", "Changua sencilla", 8000);
        yield return new(D, "Platos y especiales", "Changua especial", 13000);
        yield return new(D, "Platos y especiales", "Moñona", 14000, null, [huevos]);
        yield return new(D, "Platos y especiales", "Moñona mini", 10000, null, [huevos]);
        yield return new(D, "Desayunos completos", "Desayuno completo de costilla", 18000, null, [huevos, arroz, bebidaDesayuno]);
        yield return new(D, "Desayunos completos", "Desayuno completo de pata", 20000, null, [huevos, arroz, bebidaDesayuno]);
        yield return new(D, "Desayunos completos", "Desayuno completo de pescado", 18000, null, [huevos, arroz, bebidaDesayuno]);
        yield return new(D, "Porciones", "Porción", 2500, [new("Arroz", 2500), new("Queso", 3500), new("Costilla", 6000), new("Pata", 7000), new("Pescado", 7000)]);

        // ---- Amasijos y pastelería
        const string A = "Amasijos y Pastelería";
        yield return new(A, "Amasijos", "Almojábana", 3000);
        yield return new(A, "Amasijos", "Pandebono", 3000);
        yield return new(A, "Amasijos", "Pandebono de bocadillo", 3300);
        yield return new(A, "Amasijos", "Pan crema", 7000);
        yield return new(A, "Amasijos", "Pan de maíz", 5000);
        yield return new(A, "Amasijos", "Pan rollo", 600, PanRollo());
        yield return new(A, "Amasijos", "Mantecada", 3000, [new("Porción", 3000), new("Pequeña", 9000), new("Grande con molde", 16000), new("Grande sin molde", 16000)]);
        yield return new(A, "Amasijos", "Roscón resobado", 1500, [new("Unidad", 1500), new("Pequeño x5", 7000), new("Grande x10", 12000)]);
        yield return new(A, "Amasijos", "Despelucadas", 7000);
        yield return new(A, "Amasijos", "Galletas", 7000);
        yield return new(A, "Amasijos", "Colaciones", 5000);
        yield return new(A, "Amasijos", "Pan resobado", 7000);
        yield return new(A, "Amasijos", "Trocitos", 7000);
        yield return new(A, "Amasijos", "Arepa boyacense", 3700);
        yield return new(A, "Amasijos", "Pan de sándwich", 3000);
        yield return new(A, "Pastelería y postres", "Pastel", 6000, null, [new("Sabor", 1, 1, ["Pollo", "Chorizo", "Hawaiano", "Queso", "Salchicha"])]);
        yield return new(A, "Pastelería y postres", "Corazones / Pasabocas", 3000);
        yield return new(A, "Pastelería y postres", "Milhoja", 5500);
        yield return new(A, "Pastelería y postres", "Brazo de reina", 7000);
        yield return new(A, "Pastelería y postres", "Postre de maracuyá-mora", 6000);

        // ---- Bebidas calientes
        const string C = "Bebidas Calientes";
        yield return new(C, null, "Tinto", 2000);
        yield return new(C, null, "Café", 3000);
        yield return new(C, null, "Aromática", 2000);
        yield return new(C, null, "Aguapanela sola", 3000);
        yield return new(C, null, "Aguapanela con queso", 7000);
        yield return new(C, null, "Aguapanela en combo (queso y almojábana o pandebono)", 9000, null, [acompanante]);
        yield return new(C, null, "Chocolate solo", 3500);
        yield return new(C, null, "Chocolate con queso", 8000);
        yield return new(C, null, "Chocolate en combo (queso y almojábana o pandebono)", 11000, null, [acompanante]);
        yield return new(C, null, "Milo caliente", 8000);

        // ---- Bebidas frías
        const string F = "Bebidas Frías";
        yield return new(F, "Caseras", "Avena casera", 5000);
        yield return new(F, "Caseras", "Masato casero", 3000);
        yield return new(F, "Caseras", "Milo frío", 9000);
        yield return new(F, "Caseras", "Jugo natural", 7000, [new("En agua", 7000), new("En leche", 8000)]);
        yield return new(F, "Caseras", "Jugo de naranja", 5000);
        yield return new(F, "Agua", "Agua", 2000, [new("Mini", 2000), new("600 ml", 3500), new("1 litro", 4500)]);
        yield return new(F, "Gaseosas", "Coca-Cola", 2500, [new("Mini", 2500), new("350 ml", 3500), new("400 ml", 4500), new("600 ml", 5000), new("1,5 L", 8000), new("2,5 L", 10000), new("Lata", 4500)]);
        yield return new(F, "Gaseosas", "Quatro", 3500, [new("350 ml", 3500), new("1,5 L", 8000)]);
        yield return new(F, "Gaseosas", "Pony", 2000, [new("Mini", 2000), new("330 ml", 3500), new("1,5 L", 8000)]);
        yield return new(F, "Jugos y tés", "Jugo Hit", 4000);
        yield return new(F, "Jugos y tés", "Bretaña", 4000);
        yield return new(F, "Jugos y tés", "H2O", 5000);
        yield return new(F, "Jugos y tés", "Mr. Tea", 3500);
        yield return new(F, "Jugos y tés", "Hatsu", 6000);
        yield return new(F, "Jugos y tés", "Saviloe", 3500);
        yield return new(F, "Jugos y tés", "Frutto", 3500);
        yield return new(F, "Jugos y tés", "Soka", 3500);
        yield return new(F, "Energizantes e hidratantes", "Gatorade", 4500);
        yield return new(F, "Energizantes e hidratantes", "Vive 100", 3000, [new("Pequeño", 3000), new("Grande", 4000)]);
        yield return new(F, "Energizantes e hidratantes", "Monster", 11000);
        yield return new(F, "Energizantes e hidratantes", "Red Bull", 11000);
        yield return new(F, "Energizantes e hidratantes", "Electrolit", 10000);
        yield return new(F, "Energizantes e hidratantes", "Hydra Lite", 5000);
        yield return new(F, "Lácteos y yogures", "Yogurt / Alpina", 4000);
        yield return new(F, "Lácteos y yogures", "Yogurt Hato", 5000);
        yield return new(F, "Lácteos y yogures", "Yogurt M&M", 3500);
        yield return new(F, "Lácteos y yogures", "Mini Bonyurt", 3500);
        yield return new(F, "Lácteos y yogures", "Bonyurt", 5000);
        yield return new(F, "Lácteos y yogures", "Avena Alpina", 4500);
        yield return new(F, "Lácteos y yogures", "Yogo Yogo", 3000);
        yield return new(F, "Lácteos y yogures", "Milo en caja", 3500);
        yield return new(F, "Lácteos y yogures", "Alpin botella", 6000);
        yield return new(F, "Lácteos y yogures", "Alpin caja", 5000);
        yield return new(F, "Lácteos y yogures", "Kumis Hato", 5000);
        yield return new(F, "Lácteos y yogures", "Yogurt garrafa", 15000);
        yield return new(F, "Lácteos y yogures", "Yogurt griego", 7000);
        yield return new(F, "Lácteos y yogures", "Leche cuajada", 6000);
        yield return new(F, "Lácteos y yogures", "Gelatina", 3000);

        // ---- Sándwich y génovas
        const string S = "Sándwich y Génovas";
        yield return new(S, "Sándwiches", "Sándwich con 1 carne", 16000, null, [carne]);
        yield return new(S, "Sándwiches", "Sándwich mixto 2 carnes", 18000, null, [new("Carnes", 2, 2, ["Pernil", "Cordero", "Pavo"])]);
        yield return new(S, "Sándwiches", "Sándwich mixto 3 carnes", 20000);
        yield return new(S, "Carnes por porción", "Pernil de cerdo", 10000, [new("1/4 de libra (125 g)", 10000), new("1/2 libra (250 g)", 19000), new("1 libra (500 g)", 38000)]);
        yield return new(S, "Carnes por porción", "Pavo / Cordero", 10000, [new("1/4 de libra (125 g)", 10000), new("1/2 libra (250 g)", 19000), new("1 libra (500 g)", 38000)],
            [new("Carne", 1, 1, ["Pavo", "Cordero"])]);
        yield return new(S, "Otros", "Salchichas", 7500);
        yield return new(S, "Otros", "Kabano", 4000);
        yield return new(S, "Otros", "Génovas", 4000);

        // ---- Quesos
        const string Q = "Quesos";
        yield return new(Q, null, "Queso Pera", 4000, [new("Personal", 4000), new("230", 15000), new("430 madurado", 20000), new("450", 26000), new("x6", 22000)]);
        yield return new(Q, null, "Quesadillo", 4000, [new("Personal", 4000), new("x6", 22000)]);
        yield return new(Q, "7 Cueros", "Postre 7 cueros deditos", 15000);
        yield return new(Q, "7 Cueros", "Postre 7 cueros", 15000);
        yield return new(Q, "7 Cueros", "7 cueros 230 g", 14000);
        yield return new(Q, null, "Queso campesino", 14000);
        yield return new(Q, null, "Queso doble crema", 15000);
        yield return new(Q, null, "Queso Paipa", 22000);
        yield return new(Q, null, "Queso de cabeza", 3500);
        yield return new(Q, null, "Chicharrón de cuajada", 12000);

        // ---- Papas y pasabocas
        const string P = "Papas y Pasabocas";
        yield return new(P, null, "Todorrico", 3500);
        yield return new(P, null, "Maní", 4000, null, [new("Tipo", 1, 1, ["Normal", "Salado"])]);
        yield return new(P, null, "Maní arándano", 4000);
        yield return new(P, null, "Manimoto", 3000);
        yield return new(P, null, "Tostaco pequeño", 2500);
        yield return new(P, null, "Gas Sprait / Papas", 6000);
        yield return new(P, null, "Rizadas", 3000);
        yield return new(P, null, "Los Cuates", 10000);
        yield return new(P, null, "Chicharrón", 6000, [new("Pequeño", 6000), new("Grande", 12000)]);
        yield return new(P, null, "Achiras (bolsa)", 8000);

        // ---- Dulcería
        const string U = "Dulcería";
        yield return new(U, "Chocolates y galletas", "Snicker", 7000);
        yield return new(U, "Chocolates y galletas", "M&M", 7000);
        yield return new(U, "Chocolates y galletas", "Milky Way", 7000);
        yield return new(U, "Chocolates y galletas", "Huevo Kinder", 8500);
        yield return new(U, "Chocolates y galletas", "Jumbo", 5000, [new("Pequeña", 5000), new("Grande", 9000)]);
        yield return new(U, "Chocolates y galletas", "Gansito", 2500);
        yield return new(U, "Chocolates y galletas", "Gala", 3000);
        yield return new(U, "Chocolates y galletas", "Chocorramo", 3800);
        yield return new(U, "Chocolates y galletas", "Galletas (unidad o paquete chico)", 2000);
        yield return new(U, "Chocolates y galletas", "Mini obleas", 4000);
        yield return new(U, "Chocolates y galletas", "Tike / Like", 4500);
        yield return new(U, "Chocolates y galletas", "Achira (paquete personal)", 12000);
        yield return new(U, "Chicles y confites", "Jet", 1500);
        yield return new(U, "Chicles y confites", "Bon Bon Bum", 600);
        yield return new(U, "Chicles y confites", "Quipitos", 800);
        yield return new(U, "Chicles y confites", "Tic Tac", 3000);
        yield return new(U, "Chicles y confites", "Halls", 3000, [new("Barra", 3000), new("XS", 3500)]);
        yield return new(U, "Chicles y confites", "Trident", 2000, [new("Pequeño", 2500), new("Mediano", 3000), new("Grande", 7000), new("Tarro", 11000)]);
        yield return new(U, "Chicles y confites", "Chicle Metro", 4000);
        yield return new(U, "Chicles y confites", "Mechas locas", 5000);
        yield return new(U, "Chicles y confites", "Dr. Look peluca loca", 5000);
        yield return new(U, "Chicles y confites", "Oka Loka", 3500);
        yield return new(U, "Chicles y confites", "Cepillo", 3000);
        yield return new(U, "Chicles y confites", "Pata ácida", 5000);
        yield return new(U, "Chicles y confites", "Caramelo líquido", 5000);
        yield return new(U, "Chicles y confites", "Traga notas", 5000);
        yield return new(U, "Chicles y confites", "Gomitas Frutilla", 3500);
        yield return new(U, "Chicles y confites", "Gomitas Grissly", 3000);

        // ---- Miel y propóleo
        const string M = "Miel y Propóleo";
        yield return new(M, null, "Miel media", 20000);
        yield return new(M, null, "Miel botella", 38000);
        yield return new(M, null, "Propóleo", 20000);

        // ---- Licores
        const string L = "Licores";
        yield return new(L, "Aguardiente", "Aguardiente Antioqueño", 35000, [new("Media", 35000), new("750 ml", 60000), new("Garrafa", 70000)]);
        yield return new(L, "Aguardiente", "Aguardiente Amarillo", 35000, [new("Media", 35000), new("Botella", 65000)]);
        yield return new(L, "Otros licores", "Néctar", 32000, [new("Media", 32000), new("Litro", 63000), new("1,5 L", 68000)]);
        yield return new(L, "Otros licores", "Ron", 38000);
        yield return new(L, "Otros licores", "Vino pequeño", 3000);

        // ---- Botiquín
        const string B = "Botiquín";
        yield return new(B, null, "Acetaminofén", 300);
        yield return new(B, null, "Advil Ultra", 3500);
        yield return new(B, null, "Advil Max", 3500);
        yield return new(B, null, "Advil Gripa", 3500);
        yield return new(B, null, "Xray Dool", 2500);
        yield return new(B, null, "Sal de frutas", 4000);
        yield return new(B, null, "Bonfiest", 4000);
        yield return new(B, null, "Ibuprofeno", 800);
        yield return new(B, null, "Ibuflash", 3500);
        yield return new(B, null, "Mareol", 1200);
        yield return new(B, null, "Alka-Seltzer x2", 3000);
        yield return new(B, null, "Apronax", 3500);
        yield return new(B, null, "Aspirina x2", 4000);
        yield return new(B, null, "Dolex Forte", 2500);
        yield return new(B, null, "Dolex Gripa", 1500);
        yield return new(B, null, "Next Gripa", 3000);
    }

    private static Var[] PanRollo()
    {
        var tabla = new (int Unidades, int Precio)[]
        {
            (1, 600), (2, 1200), (4, 2400), (5, 3000), (7, 4200), (9, 5000), (10, 6000), (12, 7200), (14, 8400), (15, 9000),
            (17, 10000), (19, 11400), (20, 12000), (22, 13200), (24, 14400), (25, 15000), (27, 16200), (29, 17400),
            (30, 18000), (32, 19200), (34, 20000),
        };
        return tabla.Select(t => new Var(t.Unidades == 1 ? "1 unidad" : $"{t.Unidades} unidades", t.Precio)).ToArray();
    }
}
