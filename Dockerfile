# ── Build stage ────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY BodyshopInventory.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

# ── Runtime stage ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Copy published app
COPY --from=build /app/publish .

# Create folders for persistent data
RUN mkdir -p /app/data /app/wwwroot/uploads

# Move wwwroot into place
COPY --from=build /src/wwwroot ./wwwroot

EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000
ENV DB_PATH=/app/data/inventory.db
ENV UPLOAD_DIR=/app/wwwroot/uploads

ENTRYPOINT ["dotnet", "BodyshopInventory.dll"]
