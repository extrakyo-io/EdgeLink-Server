using EdgeLink;

// TCP 連線範例
using var client = new EdgeLinkClient("192.168.1.100", 9001);

client.OnConnected    += ()    => Console.WriteLine("[EdgeLink] Connected");
client.OnDisconnected += ()    => Console.WriteLine("[EdgeLink] Disconnected");
client.OnMessage      += msg   => Console.WriteLine($"[EdgeLink] Received: {msg}");
client.OnError        += ex    => Console.WriteLine($"[EdgeLink] Error: {ex.Message}");

client.SetAutoReconnect(true, delayMs: 5000);

await client.ConnectAsync();

// 每 3 秒送一筆資料
var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
while (await timer.WaitForNextTickAsync())
{
    if (!client.IsConnected) continue;
    await client.SendAsync("id:DOTNET_01;temp:25.3;humidity:60.0");
    Console.WriteLine("[EdgeLink] Sent sensor data");
}
