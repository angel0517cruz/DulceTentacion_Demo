# Imagen para Render: compila la aplicación y la ejecuta sobre el runtime de ASP.NET.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS compilar
WORKDIR /src

# Primero lo que cambia poco (el proyecto) para aprovechar la caché de capas
COPY src/DulceTentacion/DulceTentacion.csproj src/DulceTentacion/
COPY src/DulceTentacion/Infra/esquema.sql src/DulceTentacion/Infra/
RUN dotnet restore src/DulceTentacion/DulceTentacion.csproj

COPY src/DulceTentacion/ src/DulceTentacion/
RUN dotnet publish src/DulceTentacion/DulceTentacion.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=compilar /app .

ENV ASPNETCORE_ENVIRONMENT=Production
# Render entrega el puerto en la variable PORT (la aplicación la lee al arrancar)
EXPOSE 10000
ENTRYPOINT ["dotnet", "DulceTentacion.dll"]
