using Microsoft.AspNetCore.SignalR.Client;

var houseId = args.Length > 0 ? args[0] : "casa-dev-01";
var cameraId = args.Length > 1 ? args[1] : "192.168.0.25";

var connection = new HubConnectionBuilder()
    .WithUrl("http://localhost:5151/hubs/gateway")
    .Build();

connection.On<string>("HouseOnline", id => Console.WriteLine($"[EVENT] HouseOnline: {id}"));
connection.On<string>("HouseOffline", id => Console.WriteLine($"[EVENT] HouseOffline: {id}"));
connection.On<string, object>("CamerasUpdated", (id, cams) => Console.WriteLine($"[EVENT] CamerasUpdated: {id} -> {System.Text.Json.JsonSerializer.Serialize(cams)}"));
connection.On<string, object>("ReceiveOffer", (camId, offer) =>
{
    Console.WriteLine($"[EVENT] ReceiveOffer para camara {camId}:");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(offer, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
});
connection.On<string, string>("StreamError", (camId, reason) => Console.WriteLine($"[EVENT] StreamError camara {camId}: {reason}"));
connection.On<string, object>("ReceiveIceCandidate", (camId, candidate) => Console.WriteLine($"[EVENT] ReceiveIceCandidate camara {camId}: {System.Text.Json.JsonSerializer.Serialize(candidate)}"));

Console.WriteLine("Conectando al hub...");
await connection.StartAsync();
Console.WriteLine("Conectado. ConnectionId: " + connection.ConnectionId);

Console.WriteLine("Llamando JoinAsViewer...");
var snapshot = await connection.InvokeAsync<object>("JoinAsViewer");
Console.WriteLine("Snapshot inicial de casas:");
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"Pidiendo stream: casa={houseId} camara={cameraId}");
await connection.InvokeAsync("RequestStream", houseId, cameraId);

Console.WriteLine("Esperando eventos (10s)...");
await Task.Delay(10000);

await connection.StopAsync();
Console.WriteLine("Listo.");
