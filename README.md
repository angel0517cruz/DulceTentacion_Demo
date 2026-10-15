# Dulce Tentación · versión de demostración

Copia del sistema real de la cafetería (pedidos por celular, cuenta compartida, caja, administración y estadísticas),
preparada para mostrarse en internet. **No es la versión que usa el café**: esa corre en un computador del local,
sin internet, y no se modifica desde aquí.

## Qué cambia respecto a la versión del local
- Se publica en Render (plan gratis) con un `Dockerfile` y un `render.yaml`.
- Con `Demo__Activo=true` carga datos de ejemplo (una semana de pedidos, cobros, encuestas, gastos y cierres)
  y **todos los usuarios entran con el PIN `1234`**.
- Los datos se guardan en el contenedor: se reinician al volver a desplegar o cuando Render lo reinicia.

## Cómo probarla
1. Abrir la dirección de Render (la primera carga puede tardar hasta un minuto: el plan gratis se "duerme").
2. Tocar un nombre. Los administradores (Norberto, Lina, Miguel) ven todo; las meseras, solo pedidos y mesas.
3. PIN: `1234`.

## Correrla en el computador
```bash
cd src/DulceTentacion
Demo__Activo=true dotnet run --urls http://127.0.0.1:8080
```

## Tecnología
ASP.NET Core 10 · SQLite (Dapper) · JavaScript sin dependencias · eventos en vivo con SSE.
