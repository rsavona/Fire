using System.Net.Sockets;
using System.Text;

Console.WriteLine("=== Tura HostComm Test Client ===");
const string host = "127.0.0.1";
const int port = 6601;
const byte etx = 0x0D;

string[] messages =  {
    $"ROUT1Z59W9A50120530867 002\r\n",
    $"ROUT1Z59W9A50120532365 005\r\n", 
    $"ROUT1Z59W9A50120530866 004\r\n",
    $"ROUT1Z59W9A50120532373 004\r\n"};

try
{
    using TcpClient client = new TcpClient();
    Console.WriteLine($"Connecting to {host}:{port}...");
    await client.ConnectAsync(host, port);
    Console.WriteLine("Connected!");

    using NetworkStream stream = client.GetStream();
    
    // Start a background task to read responses
    _ = Task.Run(async () =>
    {
        byte[] buffer = new byte[1024];
        while (true)
        {
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (bytesRead == 0) break;
            
            string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[Received] {response.Replace("\r", "<CR>")}");
            Console.ResetColor();
        }
    });

    foreach (var msg in messages)
    {
        Console.WriteLine($"[Sending] {msg.Replace("\r", "<CR>")}");
        byte[] data = Encoding.UTF8.GetBytes(msg);
        await stream.WriteAsync(data, 0, data.Length);
        await Task.Delay(1000); // Wait a bit between messages
    }

    Console.WriteLine("\nAll messages sent. Waiting for any final responses (Press Ctrl+C to exit)...");
    await Task.Delay(5000);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Error: {ex.Message}");
    Console.ResetColor();
}
