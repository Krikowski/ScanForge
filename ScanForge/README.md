# Hackathon FIAP - ScanForge

## Arquitetura
- [Diagrama Draw.io](diagrama.png): API → RabbitMQ → Worker → FFmpeg/ZXing → MongoDB.
- Justificativas: Veja acima.

## Como Rodar Local
1. `dotnet restore`
2. `dotnet run`

## Docker
`docker-compose up -d`

## Testes
`dotnet test`

## Performance (Fase 03)
- Vídeos <2min: 1 FPS, ~10s processamento.
- >2min: 0.5 FPS, redução 50% tempo.

## CI/CD
Veja `.github/workflows/ci-cd.yml`.

## Bônus
- DLQ configurado.
- Serilog: Logs em `logs/`.
- Prometheus: Métricas em /metrics (porta 8081).
- SignalR: Notificações real-time.