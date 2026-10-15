using Dapper;

namespace DulceTentacion.Infra;

/// <summary>
/// Copia de seguridad automática: una por día (la primera vez que el sistema arranca ese día) en datos/respaldos,
/// y guarda las de los últimos 30 días. También se puede pedir una a mano desde Administración.
/// </summary>
public sealed class Respaldo(Db db, ILogger<Respaldo> log) : BackgroundService
{
    private string Carpeta => Path.Combine(db.Carpeta, "respaldos");

    public async Task<string> Crear()
    {
        Directory.CreateDirectory(Carpeta);
        var ruta = Path.Combine(Carpeta, $"dulce_{DateTime.Now:yyyyMMdd_HHmmss}.db");
        await using var cn = await db.Abrir();
        await cn.ExecuteAsync($"VACUUM INTO '{ruta.Replace("'", "''")}'");   // copia consistente aunque el sistema esté en uso
        return ruta;
    }

    public IEnumerable<FileInfo> Existentes() =>
        Directory.Exists(Carpeta)
            ? new DirectoryInfo(Carpeta).GetFiles("dulce_*.db").OrderByDescending(f => f.CreationTime)
            : [];

    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        while (!parar.IsCancellationRequested)
        {
            try
            {
                var hoy = DateTime.Now.Date;
                if (!Existentes().Any(f => f.CreationTime.Date == hoy))
                {
                    var ruta = await Crear();
                    log.LogInformation("Respaldo diario creado: {Ruta}", ruta);
                }
                foreach (var viejo in Existentes().Where(f => f.CreationTime < DateTime.Now.AddDays(-30))) viejo.Delete();
            }
            catch (Exception ex) { log.LogError(ex, "No se pudo crear el respaldo diario"); }
            await Task.Delay(TimeSpan.FromHours(3), parar).ContinueWith(_ => { });
        }
    }
}
