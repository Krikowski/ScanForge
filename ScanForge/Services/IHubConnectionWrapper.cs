using Microsoft.AspNetCore.SignalR.Client;
using System.Threading.Tasks;

namespace ScanForge.Services;

/// <summary>
/// Abstração para permitir testes unitários de SignalR sem depender de HubConnection real.
/// </summary>
public interface IHubConnectionWrapper {
    HubConnectionState State { get; }
    Task InvokeAsync(string methodName, object? arg);
    Task DisposeAsync();
}
