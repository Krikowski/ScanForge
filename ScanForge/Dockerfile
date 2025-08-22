# Etapa de build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copia apenas o csproj e restaura dependências
COPY ["ScanForge.csproj", "./"]
RUN dotnet restore "./ScanForge.csproj"

# Copia todo o código e builda
COPY . .
RUN dotnet build "./ScanForge.csproj" -c Release -o /app/build

# Publica a aplicação
RUN dotnet publish "./ScanForge.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Etapa final
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
EXPOSE 8081
ENTRYPOINT ["dotnet", "ScanForge.dll"]
