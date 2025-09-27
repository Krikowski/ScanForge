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
1. Framework e Linguagem: .NET 8.0
Ideal para workloads CPU/I/O intensivos como processamento de vídeo.
Suporte a AOT compilation para reduzir latência.
Integração nativa com FFMpegCore para extração de frames.
Fácil integração com bibliotecas de QR Code (ex.: ZXing.NET).
Amplo suporte enterprise e cloud (Azure, AWS ECS).

2. Arquitetura Orientada a Serviços e Microsserviços
O ScanForge roda isolado do VideoNest, mas ambos se comunicam via RabbitMQ, garantindo desacoplamento.
Services, Repositories, DTOs e Controllers organizam o código de forma clara, aplicando SOLID.
Caso o processamento falhe, o upload continua operando normalmente (resiliência).

3. Mensageria com RabbitMQ
Recebe mensagens do VideoNest e inicia processamento assíncrono.
Implementa Dead Letter Queue (DLQ) e retry policies, garantindo robustez em falhas.
Justificado sobre Kafka pela simplicidade e menor overhead.

4. Armazenamento e Persistência
MongoDB: flexibilidade para armazenar resultados de análise (QR Codes e timestamps).
Redis: suporte a cache para status de processamento, acelerando consultas.

5. Monitoramento e Notificações
Prometheus: coleta métricas de tempo de processamento, taxa de sucesso e falhas.
SignalR: envio em tempo real de atualizações de status (UX aprimorada, evita polling).

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
