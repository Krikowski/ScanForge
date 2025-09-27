# ScanForge

ScanForge - microservice para processamento de vídeos, extração de frames e identificação de qrCodes.

## Introdução ao ScanForge

O ScanForge foi concebido como o coração de processamento da solução de vídeos desenvolvida no Hackathon FIAP 7NETT. Enquanto o VideoNest funciona como um “ninho de vídeos” para upload e organização, o ScanForge é a forja de escaneamento, onde ocorre o trabalho pesado: extração de frames, detecção e decodificação de QR Codes, atualização de status e geração de resultados analíticos.
O nome “ScanForge” nasce dessa metáfora: assim como uma forja transforma matéria bruta em algo útil e valioso através de intenso calor e energia, o ScanForge transforma vídeos crus em informações úteis (QR Codes e metadados), entregando valor real e acionável para usuários e sistemas.
Esse microsserviço roda de forma independente, mas se comunica com o VideoNest via RabbitMQ (mensageria assíncrona), compondo juntos uma arquitetura de microsserviços escalável e resiliente para análise de conteúdo de vídeo.

## Visão Geral do Projeto

O ScanForge foi desenvolvido em .NET 8.0, utilizando arquitetura orientada a serviços (SOA) e práticas modernas de engenharia para lidar com processamento pesado.

## Funcionalidades principais (RF3 – RF5 do Hackathon):

Extração de frames dos vídeos recebidos via fila RabbitMQ.
Detecção e decodificação de QR Codes em cada frame processado.
Atualização de status do processamento (Na Fila → Processando → Concluído → Erro).
Armazenamento de resultados (conteúdo dos QR Codes + timestamps) no MongoDB.
Notificação em tempo real para clientes via SignalR (bônus).
Métricas de performance via Prometheus (bônus).

## Justificativas Técnicas: Decisões Baseadas no Mercado

1. .NET 8.0
A escolha do .NET 8.0 atende diretamente ao requisito de uso de uma linguagem madura e performática para workloads de processamento de vídeo (RF3–RF5).
Esse framework é ideal para workloads I/O-bound (como leitura/escrita de vídeos) e CPU-bound (decodificação e análise de frames), entregando alta performance e baixo consumo de recursos.
No código, isso aparece em métodos como:
  var frames = await _frameExtractor.ExtractFramesAsync(message.FilePath);
  var qrCodes = _qrDecoder.Decode(frame);
➝ Aqui, a combinação de FFMpegCore e ZXing.NET aproveita bibliotecas consolidadas para extração de frames e leitura de QR Codes.
Bancos como Itaú e Bradesco usam .NET em sistemas core pela confiabilidade, suporte enterprise e performance em larga escala. Isso reforça que o ScanForge usa uma base tecnológica de nível corporativo.

2. Arquitetura de Microsserviços
O ScanForge roda isolado do VideoNest, comunicando-se apenas por mensagens assíncronas (RabbitMQ). Essa separação garante resiliência: se o processamento falhar, o upload segue disponível.
Essa escolha cumpre o requisito de arquitetura orientada a serviços e permite escalabilidade horizontal: múlticas instâncias de ScanForge podem processar vídeos em paralelo.
Esse padrão é usado em empresas como o Spotify, onde serviços independentes (streaming, playlists, anúncios) podem escalar ou falhar sem impactar o sistema como um todo. O mesmo princípio foi aplicado aqui.

3. RabbitMQ (Mensageria)
O RabbitMQ gerencia a fila de vídeos enviados pelo VideoNest, garantindo processamento assíncrono conforme os RFs exigidos no Hackathon.
Implementamos retry automático e Dead Letter Queue (DLQ), atendendo boas práticas de resiliência exigidas em produção.
No código, o consumo das mensagens acontece de forma desacoplada:
public async Task ProcessMessage(VideoMessage message)
  {
      await _videoProcessingService.ProcessVideoAsync(message);
  }
Escolhemos RabbitMQ ao invés de Kafka pela simplicidade e leveza em workloads de volume médio (como uploads em lote).
Esse mesmo modelo é usado por grandes e-commerces durante a Black Friday, para filas de pedidos – provando que a tecnologia suporta picos de demanda.

4. MongoDB + Redis (Armazenamento e Cache)
O MongoDB armazena resultados flexíveis do processamento (conteúdo do QR Code + timestamp). Por ser NoSQL, suporta modelos variáveis de dados, ideal para vídeos que podem ter 0, 1 ou múltiplos QR Codes.
O Redis entra como camada de cache de status, acelerando consultas frequentes e reduzindo latência, em conformidade com o requisito de respostas rápidas ao usuário (RF6 e RF7).
Netflix usa MongoDB para armazenar metadados de mídia (catálogos, streams).
Twitter usa Redis para armazenar timelines e caches em tempo real.
➝ A mesma lógica foi aplicada no ScanForge: MongoDB garante flexibilidade dos resultados, e Redis garante performance em consultas recorrentes.

5. Prometheus + Logging Estruturado (Observabilidade)
O Prometheus coleta métricas customizadas como tempo médio de processamento de um vídeo e taxa de sucesso/falhas, permitindo alertas proativos em cenários de produção.
Exemplo no código:
  _metrics.ObserveProcessingTime(processingDuration);
O Serilog garante logs estruturados, com correlação de eventos (ex.: ID do vídeo + status). Isso atende ao requisito de boas práticas de arquitetura e código limpo do Hackathon.
O ScanForge adota essa prática para oferecer rastreabilidade semelhante em escala.

## Aplicação de Princípios de Engenharia de Software

### Clean Code
Nomes claros (VideoResult, ProcessVideoAsync), responsabilidades isoladas, logging estruturado.

### KISS
Foco em manter fluxos de processamento simples e diretos.

### YAGNI
Apenas RFs necessários e bônus úteis foram implementados, evitando sobrecarga.

### DDD
Entidades modeladas em torno do domínio (VideoResult, QRCodeResult).

### SOLID
SRP: cada serviço faz apenas uma coisa (ex.: VideoProcessingService).
OCP: regras de processamento abertas para extensão (novos tipos de análise).
DIP: dependências injetadas, facilitando testes e mocks.

## Testes Unitários: Garantia de Segurança e Confiabilidade
Cobertura de Áreas Críticas
Controllers: validação do health check (status OK, estrutura, logs).
DTOs: integridade e serialização de mensagens (VideoMessage).
Models: consistência de entidades (VideoResult, QRCodeResult, estados válidos).
Repositório MongoDB: persistência de vídeos, atualização de status, adição de QR Codes, criação de índices.

### Serviços:
SignalRNotifierService: notificações, retries e fallback HTTP.
VideoProcessingService: processamento válido/inválido, logs e atualização de status.

### Impacto dos Testes

Garantem resiliência contra falhas (arquivos inválidos, indisponibilidade do SignalR).
Aumentam a confiança em produção para fluxo ponta-a-ponta.
Validam logs estruturados, fortalecendo auditoria e rastreabilidade.
Permitem evolução do sistema com baixa chance de regressão.

### Quantidade de Testes
Controllers → 2
DTOs → 2
Models → 3
Repositório → 6
Serviços SignalR → 8
Serviço de Processamento → 3+
Total aproximado: 25 testes unitários, com foco em pontos críticos.

### Valor Agregado
Proteção contra falhas críticas.
Confiança para integração com VideoNest.
Documentação executável do comportamento esperado.
Preparação para escala e produção.

### Instalação e Uso

Requisitos: Docker, .NET SDK 8.0, RabbitMQ, MongoDB.

Build e Execução
dotnet run
ou via Docker
docker build -t scanforge .
docker run -p 8080:8080 scanforge

Endpoints

GET /health → status do serviço.
POST /process → recebe mensagens do RabbitMQ (interno).
GET /results/{id} → consulta resultados do vídeo.

Swagger

Disponível em /swagger.
