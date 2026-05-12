using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.GenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

// 1. Load Configuration
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

string apiKey = config["AI:ApiKey"] ?? "";
string modelId = config["AI:Model"] ?? "gemini-1.5-flash";

Console.Clear();
Console.WriteLine("========================================");
Console.WriteLine("   FORTNA FUSION - TOKAMAK AI BUILDER");
Console.WriteLine("========================================");
Console.WriteLine();

while (true)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("SELECT MODE:");
    Console.WriteLine("1. [ONLINE] Create New Blueprint (requires AI)");
    Console.WriteLine("2. [OFFLINE] Provision Blueprint (scan for hardware)");
    Console.WriteLine("3. Exit");
    Console.Write("\nChoice: ");
    Console.ResetColor();

    var choice = Console.ReadLine();
    if (choice == "1") await RunAiMode();
    else if (choice == "2") await RunProvisionMode();
    else if (choice == "3") break;
}

async Task RunAiMode()
{
    if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_GEMINI_API_KEY_HERE")
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Error: Gemini API Key not found in appsettings.json. AI Mode disabled.");
        Console.ResetColor();
        return;
    }

    var googleClient = new Client(apiKey: apiKey);
    IChatClient chatClient = googleClient.AsIChatClient(modelId);

    Console.Write("\nEnter Customer Name: ");
    string customerName = Console.ReadLine()?.Trim() ?? "Unknown";

    // Dynamic AI Giggle
    try
    {
        var giggleResponse = await chatClient.GetResponseAsync($"Give me a very short, witty, one-sentence pun or industry joke about the company '{customerName}'. Keep it professional but funny. No markdown.");
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"\n>>> {giggleResponse.Text?.Trim()}\n");
        Console.ResetColor();
    }
    catch { }

    string systemPrompt = $$"""
    You are a configuration expert for the Fortna Fusion automation system. Generate valid ".fusion" JSON blueprints.
    - Terminology: Elements (Devices), Forces (Workflows), Bonds (Routes).
    - Structure: AppSettings -> DeviceSpace -> Cores [ { Name, Elements [], Forces [] } ].
    - Managers: [ActiveMQManager, VirtualPlcManager, PlcDeviceManager, AbCipPlcManager, PrinterManager, PrintClientManager, DiagnosticDeviceManager].
    - AbCipPlcManager Properties: "IPAddress" (e.g. 192.168.1.10), "CpuType" (lgx), "Path" (1,0), "RequestTag" (string), "ResponseTag" (string), "UpdateTag" (string).
    - Return ONLY raw JSON.
    """;

    while (true)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("Describe the system (or 'back'): ");
        Console.ResetColor();
        string? input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input) || input.ToLower() == "back") break;

        Console.WriteLine("Thinking...");
        try
        {
            var response = await chatClient.GetResponseAsync(new List<ChatMessage> { new(ChatRole.System, systemPrompt), new(ChatRole.User, input) });
            string jsonResult = response.Text?.Trim() ?? "";
            var node = JsonNode.Parse(jsonResult);
            string fileName = $"{customerName}.fusion";
            await File.WriteAllTextAsync(fileName, jsonResult);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\nSuccess! Created {fileName}\n");
            Console.ResetColor();
        }
        catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }
    }
}

async Task RunProvisionMode()
{
    var fusionFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.fusion");
    if (fusionFiles.Length == 0)
    {
        Console.WriteLine("\nNo .fusion files found in the current directory.");
        return;
    }

    Console.WriteLine("\nAvailable Blueprints:");
    for (int i = 0; i < fusionFiles.Length; i++) Console.WriteLine($"{i + 1}. {Path.GetFileName(fusionFiles[i])}");
    Console.Write("Select a file: ");
    if (!int.TryParse(Console.ReadLine(), out int fileIdx) || fileIdx < 1 || fileIdx > fusionFiles.Length) return;

    string filePath = fusionFiles[fileIdx - 1];
    var json = await File.ReadAllTextAsync(filePath);
    var root = JsonNode.Parse(json);
    var elements = root?["AppSettings"]?["DeviceSpace"]?["Elements"]?.AsArray();

    if (elements == null) { Console.WriteLine("Invalid blueprint format."); return; }

    var printers = elements.Where(e => e?["Manager"]?.GetValue<string>().Contains("Printer") == true 
                                     || e?["Manager"]?.GetValue<string>().Contains("PrintClient") == true).ToList();

    if (!printers.Any())
    {
        Console.WriteLine("\nNo printer elements found in this blueprint to provision.");
        return;
    }

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\nFound {printers.Count} printers to provision.");
    Console.Write("Enter Subnet to scan (e.g., 10.33.218.0): ");
    Console.ResetColor();
    string subnet = Console.ReadLine()?.Trim() ?? "";
    if (string.IsNullOrEmpty(subnet)) return;

    string baseIp = string.Join(".", subnet.Split('.').Take(3)) + ".";
    Console.WriteLine($"\nScanning {baseIp}1-254 for printers (Port 9100)...");

    var foundIps = new List<string>();
    var tasks = new List<Task>();
    using var semaphore = new SemaphoreSlim(50); // Parallelism limit

    for (int i = 1; i <= 254; i++)
    {
        string targetIp = baseIp + i;
        tasks.Add(Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            try
            {
                using var client = new TcpClient();
                var result = client.BeginConnect(targetIp, 9100, null, null);
                var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(200));
                if (success)
                {
                    lock (foundIps) foundIps.Add(targetIp);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"  [FOUND] {targetIp}");
                    Console.ResetColor();
                }
            }
            catch { }
            finally { semaphore.Release(); }
        }));
    }

    await Task.WhenAll(tasks);

    if (foundIps.Count == 0)
    {
        Console.WriteLine("\nNo printers discovered on this subnet.");
        return;
    }

    Console.WriteLine($"\nDiscovered {foundIps.Count} printers. Assigning to blueprint...");

    for (int i = 0; i < printers.Count; i++)
    {
        if (i < foundIps.Count)
        {
            var printer = printers[i];
            var props = printer?["Properties"]?.AsObject();
            if (props != null)
            {
                props["IPAddress"] = foundIps[i];
                Console.WriteLine($"  Assigned {foundIps[i]} to {printer?["Name"]}");
            }
        }
    }

    await File.WriteAllTextAsync(filePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\nBlueprint {Path.GetFileName(filePath)} updated with discovered hardware!");
    Console.ResetColor();
}
