# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Alento.Web/Alento.Web.csproj src/Alento.Web/
RUN dotnet restore src/Alento.Web/Alento.Web.csproj
COPY src/ src/
RUN dotnet publish src/Alento.Web/Alento.Web.csproj -c Release -o /app --no-restore

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DataProtection__Pasta=/keys \
    TZ=America/Sao_Paulo
RUN mkdir -p /keys && chown -R app:app /keys
COPY --from=build /app .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Alento.Web.dll"]
