FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app
COPY . .
RUN dotnet restore ScanForge.csproj
RUN dotnet publish ScanForge.csproj -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/out .
RUN apt-get update && apt-get install -y ffmpeg libgdiplus
ENV ASPNETCORE_ENVIRONMENT=Development
ENTRYPOINT ["dotnet", "ScanForge.dll"]