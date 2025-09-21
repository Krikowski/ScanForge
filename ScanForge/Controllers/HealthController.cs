using Microsoft.AspNetCore.Mvc;

namespace ScanForge.Controllers;

/// <summary>
/// Controller para health checks do ScanForge Worker Service
/// </summary>
[ApiController]
[Route("[controller]")]
public class HealthController : ControllerBase {
    private readonly ILogger<HealthController> _logger;

    /// <summary>
    /// Inicializa o HealthController com logger para monitoramento
    /// </summary>
    /// <param name="logger">Instância do logger estruturado</param>
    public HealthController(ILogger<HealthController> logger) {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Endpoint de health check para validação de serviço
    /// </summary>
    /// <remarks>
    /// Usado por:
    /// - Docker healthcheck no compose
    /// - Prometheus scraping de métricas
    /// - Monitoramento de dependências
    /// 
    /// Retorna HTTP 200 quando o serviço está saudável
    /// </remarks>
    /// <returns>Status JSON com timestamp e nome do serviço</returns>
    [HttpGet]
    public IActionResult Get() {
        _logger.LogInformation("🔍 Health check executado: {Timestamp}", DateTime.UtcNow);
        return Ok(new {
            status = "Healthy",
            timestamp = DateTime.UtcNow,
            service = "ScanForge Worker"
        });
    }
}