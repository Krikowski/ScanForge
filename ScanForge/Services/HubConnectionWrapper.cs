using Microsoft.AspNetCore.SignalR.Client;
using System.Threading.Tasks;

namespace ScanForge.Services;

public class HubConnectionWrapper : IHubConnectionWrapper {
    private readonly HubConnection _hubConnection;

    public HubConnectionWrapper(HubConnection hubConnection) {
        _hubConnection = hubConnection;
    }

    public HubConnectionState State => _hubConnection.State;

    public Task InvokeAsync(string methodName, object? arg) =>
        _hubConnection.InvokeAsync(methodName, arg);

    public Task DisposeAsync() => _hubConnection.DisposeAsync().AsTask();
}
