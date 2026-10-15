namespace DulceTentacion.Infra;

/// <summary>Validaciones de entrada con mensajes pensados para quien usa el sistema.</summary>
public static class V
{
    public static string Texto(string? valor, string campo, int max)
    {
        var t = (valor ?? "").Trim();
        if (t.Length == 0) throw new NegocioException($"{campo} es obligatorio.");
        if (t.Length > max) throw new NegocioException($"{campo} no puede pasar de {max} caracteres.");
        return t;
    }

    public static string? TextoOpc(string? valor, string campo, int max)
    {
        var t = (valor ?? "").Trim();
        if (t.Length == 0) return null;
        if (t.Length > max) throw new NegocioException($"{campo} no puede pasar de {max} caracteres.");
        return t;
    }

    public static int Dinero(long? valor, string campo, bool permitirCero = true)
    {
        if (valor is null) throw new NegocioException($"{campo} es obligatorio.");
        if (valor < 0 || (!permitirCero && valor == 0)) throw new NegocioException($"{campo} debe ser mayor que cero.");
        if (valor > 100_000_000) throw new NegocioException($"{campo} es demasiado grande.");
        return (int)valor.Value;
    }

    public static int Entero(int? valor, string campo, int minimo = 0, int maximo = 100_000)
    {
        if (valor is null) throw new NegocioException($"{campo} es obligatorio.");
        if (valor < minimo) throw new NegocioException($"{campo} no puede ser menor que {minimo}.");
        if (valor > maximo) throw new NegocioException($"{campo} no puede ser mayor que {maximo:N0}.");
        return valor.Value;
    }

    public static string Opcion(string? valor, string campo, params string[] validas)
    {
        var t = (valor ?? "").Trim();
        return validas.FirstOrDefault(v => string.Equals(v, t, StringComparison.OrdinalIgnoreCase))
            ?? throw new NegocioException($"{campo} no es válido.");
    }

    /// <summary>PIN numérico de 4 a 8 dígitos.</summary>
    public static string Pin(string? valor)
    {
        var t = (valor ?? "").Trim();
        if (t.Length < 4 || t.Length > 8 || !t.All(char.IsDigit))
            throw new NegocioException("El PIN debe tener entre 4 y 8 números.");
        return t;
    }

    /// <summary>
    /// Una opción puede llevar recargo: "Rancheros +2000" (también "Rancheros|2000" o "+$2.000").
    /// Devuelve el nombre limpio y el recargo en pesos.
    /// </summary>
    public static (string Nombre, int Extra) OpcionConExtra(string? valor)
    {
        var t = (valor ?? "").Trim();
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^(.*?)\s*(?:\+|\|)\s*\$?\s*([\d\.,]+)\s*$");
        if (!m.Success) return (t, 0);
        var digitos = new string(m.Groups[2].Value.Where(char.IsDigit).ToArray());
        if (digitos.Length == 0 || digitos.Length > 7) return (t, 0);
        var nombre = m.Groups[1].Value.Trim();
        return nombre.Length == 0 ? (t, 0) : (nombre, int.Parse(digitos));
    }
}
