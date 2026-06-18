using EdgeLink;

// UDP 傳送範例
using var sender = new EdgeLinkUdpSender();

var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
Console.WriteLine("[EdgeLink] Sending UDP packets every 3s. Ctrl+C to stop.");

while (await timer.WaitForNextTickAsync())
{
    await sender.SendAsync("192.168.1.100", 9002, "id:DOTNET_01;temp:25.3;humidity:60.0");
    Console.WriteLine("[EdgeLink] UDP sent");
}
