using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace DulceTentacion.Infra;

/// <summary>
/// Avisa en tiempo real a todos los celulares y computadores conectados cuando algo cambia
/// (una mesa se ocupa, llega un pedido, se cobra). Usa eventos del servidor (SSE): no necesita internet.
/// </summary>
public sealed class Eventos
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _clientes = new();

    public (Guid Id, ChannelReader<string> Lector) Suscribir()
    {
        var id = Guid.NewGuid();
        var canal = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        _clientes[id] = canal;
        return (id, canal.Reader);
    }

    public void Quitar(Guid id)
    {
        if (_clientes.TryRemove(id, out var canal)) canal.Writer.TryComplete();
    }

    public void Emitir(string tipo, object? datos = null)
    {
        var json = JsonSerializer.Serialize(new { t = tipo, d = datos });
        foreach (var canal in _clientes.Values) canal.Writer.TryWrite(json);
    }
}
