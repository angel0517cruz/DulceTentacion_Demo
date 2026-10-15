using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace DulceTentacion.Infra;

/// <summary>
/// Acceso a la base de datos SQLite. La base es un solo archivo (datos/dulce_tentacion.db):
/// para respaldarla basta copiarlo, y no necesita instalar ningún servidor.
/// </summary>
public sealed class Db
{
    private readonly string _cadena;
    public string Carpeta { get; }
    public string Ruta { get; }

    public Db(IConfiguration cfg, IWebHostEnvironment env)
    {
        Carpeta = cfg["Datos:Carpeta"] is { Length: > 0 } c ? c : Path.Combine(env.ContentRootPath, "datos");
        Directory.CreateDirectory(Carpeta);
        Ruta = Path.Combine(Carpeta, "dulce_tentacion.db");
        _cadena = new SqliteConnectionStringBuilder
        {
            DataSource = Ruta,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 15,        // si otra persona está guardando, espera en vez de fallar
            Pooling = true,
        }.ToString();
    }

    public async Task<SqliteConnection> Abrir()
    {
        var cn = new SqliteConnection(_cadena);
        await cn.OpenAsync();
        await cn.ExecuteAsync("PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;");
        return cn;
    }

    /// <summary>Todo o nada. Toma el candado de escritura al empezar, así dos cobros a la vez no se pisan.</summary>
    public async Task<T> Transaccion<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> trabajo)
    {
        await using var cn = await Abrir();
        await using var tx = cn.BeginTransaction(deferred: false);
        try
        {
            var resultado = await trabajo(cn, tx);
            await tx.CommitAsync();
            return resultado;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>Crea las tablas que falten y deja la base en modo WAL (lecturas y escrituras a la vez).</summary>
    public async Task Preparar()
    {
        await using var cn = await Abrir();
        await cn.ExecuteAsync("PRAGMA journal_mode = WAL;");
        using var flujo = Assembly.GetExecutingAssembly().GetManifestResourceStream("esquema.sql")
            ?? throw new InvalidOperationException("No se encontró el esquema dentro de la aplicación.");
        using var lector = new StreamReader(flujo);
        await cn.ExecuteAsync(await lector.ReadToEndAsync());
        // Bases creadas antes de que las opciones tuvieran recargo: se agrega la columna sin perder datos.
        if (await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('opcion') WHERE name = 'precio_extra'") == 0)
            await cn.ExecuteAsync("ALTER TABLE opcion ADD COLUMN precio_extra INTEGER NOT NULL DEFAULT 0");
    }

    public static string Ahora() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public static string Hoy() => DateTime.Now.ToString("yyyy-MM-dd");
}
