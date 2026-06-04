using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using WordColor = DocumentFormat.OpenXml.Wordprocessing.Color;
using WordText = DocumentFormat.OpenXml.Wordprocessing.Text;
using WordStyle = DocumentFormat.OpenXml.Wordprocessing.Style;
using UglyToad.PdfPig;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Json;
using File = System.IO.File;
using Directory = System.IO.Directory;
using Path = System.IO.Path;

// 1. Load Configuration
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .Build();

string apiKey = config["AI:ApiKey"] ?? System.Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
string modelId = config["AI:Model"] ?? "gemini-1.5-pro-latest";
if (modelId.Contains("Gemini 3", StringComparison.OrdinalIgnoreCase)) modelId = "gemini-1.5-pro-latest";

Console.Clear();
AnsiConsole.Write(new FigletText("TOKAMAK AI").Centered().Color(Spectre.Console.Color.Orange1));
AnsiConsole.Write(new Rule("[yellow]FORTNA FUSION BLUEPRINT BUILDER[/]").RuleStyle("grey").Centered());
Console.WriteLine();

while (true)
{
    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[yellow]SELECT MODE:[/]")
            .PageSize(10)
            .AddChoices(new[] {
                "1. [[ONLINE]] Create New Blueprint (Interactive)",
                "2. [[DOC]]    Build from Scope Document (.docx / .pdf / .txt)",
                "3. [[OFFLINE]] Provision Blueprint (scan for hardware)",
                "4. Exit"
            }));

    if (choice.StartsWith("1")) await RunAiMode();
    else if (choice.StartsWith("2")) await RunDocumentMode();
    else if (choice.StartsWith("3")) await RunProvisionMode();
    else if (choice.StartsWith("4")) break;
}

async Task RunAiMode()
{
    if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_GEMINI_API_KEY_HERE")
    {
        AnsiConsole.MarkupLine("[red]Error: Gemini API Key not found in appsettings.json. AI Mode disabled.[/]");
        return;
    }

    var googleClient = new Client(apiKey: apiKey, httpOptions: new HttpOptions { ApiVersion = "v1beta" });
    IChatClient chatClient = googleClient.AsIChatClient(modelId);

    string customerName = AnsiConsole.Ask<string>("[cyan]Enter Customer Name:[/] ");
    string systemPrompt = GetSystemPrompt(customerName);
    string aiGiggle = "";
    string lastJsonResult = "";

    // Dynamic AI Giggle
    try
    {
        var giggleResponse = await chatClient.GetResponseAsync(new List<ChatMessage> 
        { 
            new(ChatRole.User, $"SYSTEM INSTRUCTIONS:\n{systemPrompt}\n\nUSER REQUEST: Give me a very short, witty, one-sentence pun or industry joke about the company '{customerName}'. Keep it professional but funny. No markdown.")
        });
        aiGiggle = giggleResponse.Text?.Trim() ?? "";
    }
    catch { }

    while (true)
    {
        Console.Clear();
        
        // Create the Layout
        var layout = new Layout("Root")
            .SplitColumns(
                new Layout("Left").Ratio(2),
                new Layout("Right").Ratio(1)
            );

        // Catalog Content
        var catalog = GetCatalogPanel();
        layout["Right"].Update(catalog);

        // Compact Header (Title + Pun)
        var header = new Panel(new Markup($"[bold cyan]{customerName}[/] [grey]Fusion Builder[/] - [italic magenta1]{aiGiggle.Replace("[", "[[").Replace("]", "]]")}[/]"))
            .Border(BoxBorder.Rounded)
            .BorderColor(Spectre.Console.Color.Grey);

        // JSON Preview (If exists)
        IRenderable bodyContent;
        if (!string.IsNullOrEmpty(lastJsonResult))
        {
            bodyContent = new Rows(
                new Panel(new JsonText(lastJsonResult))
                    .Header("[yellow]Generated Blueprint[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Spectre.Console.Color.Yellow)
                    .Expand(),
                new Panel(new Markup("[cyan]Describe adjustments below or 'back' to exit.[/]"))
                    .Border(BoxBorder.None)
            );
        }
        else
        {
            bodyContent = new Panel(new Markup(
                    "[cyan]Describe your system below.[/]\n" +
                    "[grey]Example: 'Create a system with an ActiveMQ broker, 2 Zebra printers, and a standard sort reaction.'[/]\n" +
                    "[grey]Type 'back' to return to menu.[/]"))
                    .Border(BoxBorder.None);
        }
        
        layout["Left"].Update(
            new Rows(
                header,
                bodyContent
            )
        );

        AnsiConsole.Write(layout);

        string input = AnsiConsole.Ask<string>("\n[bold cyan]Description >[/] ");
        if (string.IsNullOrWhiteSpace(input) || input.ToLower() == "back") break;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("AI is thinking...", async ctx => 
            {
                try
                {
                    string hints = LoadHints();
                    var response = await chatClient.GetResponseAsync(new List<ChatMessage> 
                    { 
                        new(ChatRole.User, $"SYSTEM INSTRUCTIONS:\n{systemPrompt}\n\nEXTRA HINTS/CONSTRAINTS:\n{hints}\n\nUSER REQUEST: {input}") 
                    });
                    string jsonResult = response.Text?.Trim() ?? "";
                    
                    if (jsonResult.Contains("```json")) jsonResult = jsonResult.Split("```json")[1].Split("```")[0].Trim();
                    else if (jsonResult.StartsWith("```")) jsonResult = jsonResult.Replace("```", "").Trim();

                    lastJsonResult = jsonResult; // Store for preview
                    var node = JsonNode.Parse(jsonResult);
                    string fileName = $"fbp_{customerName.Replace(" ", "_")}.json";
                    await File.WriteAllTextAsync(fileName, jsonResult);
                    
                    ctx.Status("Estimating Commission Time...");
                    var estResponse = await chatClient.GetResponseAsync(new List<ChatMessage> 
                    { 
                        new(ChatRole.User, $"Based on the complexity, hardware, and logic in this Fortna Fusion configuration, give a 1 to 2 sentence estimate of how long it will take to commission (install, configure, test, validate) this system on site. Just give the estimate directly.\n\nCONFIG:\n{jsonResult}") 
                    });
                    string estimate = estResponse.Text?.Trim() ?? "Estimate unavailable.";

                    AnsiConsole.MarkupLine($"\n[green]Success! Created {fileName}[/]");
                    AnsiConsole.MarkupLine($"[cyan]Estimated Time to Commission: {estimate}[/]\n");
                    
                    StartFusionConsole(fileName);
                }
                catch (Exception ex) { AnsiConsole.WriteException(ex); Console.ReadLine(); }
            });
    }
}

Panel GetCatalogPanel()
{
    var grid = new Grid().AddColumn();

    grid.AddRow(new Markup("[yellow bold]ELEMENT MANAGERS[/]"));
    string[] managers = {
        "ActiveMqManager", "ActiveMqBrowserManager", "ActiveMqQueuePeekManager", "AiElementManager",
        "ApiDeviceManager", "DatabaseElementManager", "RedisCacheManager", "TelemetryManager",
        "InventoryManager", "FireLogManager", "FileMessageElementManager", "TcpMessageClientElementManager",
        "TcpMessageServerElementManager", "MqttManager", "NatsManager", "EmailManager",
        "PlcElementManager", "AbCipPlcManager", "VirtualPlcManager", "PrinterManager",
        "PrintClientManager", "ScannerSimulatorManager", "ScannerClientManager", "DiagnosticElementManager",
        "BlueprintVerifierManager"
    };
    foreach (var m in managers.OrderBy(x => x)) grid.AddRow(new Spectre.Console.Text($"• {m}", new Spectre.Console.Style(Spectre.Console.Color.Grey)));

    grid.AddRow(new Spectre.Console.Text(" "));
    grid.AddRow(new Markup("[yellow bold]REACTION TYPES[/]"));
    string[] reactions = {
        "ReactionSimulation", "PrintAndApplyFrc", "StandardSort", "HostOutputReaction"
    };
    foreach (var r in reactions.OrderBy(x => x)) grid.AddRow(new Spectre.Console.Text($"• {r}", new Spectre.Console.Style(Spectre.Console.Color.Grey)));

    return new Panel(grid)
        .Header("[blue]Catalog[/]")
        .Border(BoxBorder.Double);
}

async Task RunDocumentMode()
{
    if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_GEMINI_API_KEY_HERE")
    {
        AnsiConsole.MarkupLine("[red]Error: Gemini API Key not found. Document Mode disabled.[/]");
        return;
    }

    var docFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.*")
        .Where(f => f.EndsWith(".docx") || f.EndsWith(".pdf") || f.EndsWith(".txt") || f.EndsWith(".md"))
        .ToList();

    string docPath = "";
    if (docFiles.Any())
    {
        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select a Scope Document:[/]")
                .AddChoices(docFiles.Select(f => Path.GetFileName(f) ?? "").Concat(new[] { "Enter Path Manually" })));

        if (selection == "Enter Path Manually")
        {
            docPath = AnsiConsole.Ask<string>("[cyan]Paste file path:[/]").Replace("\"", "").Trim();
        }
        else
        {
            docPath = docFiles.First(f => Path.GetFileName(f) == selection);
        }
    }
    else
    {
        docPath = AnsiConsole.Ask<string>("[cyan]No docs found. Enter file path:[/]").Replace("\"", "").Trim();
    }

    if (string.IsNullOrEmpty(docPath) || !File.Exists(docPath)) { AnsiConsole.MarkupLine("[red]File not found.[/]"); return; }

    string rawText = "";
    await AnsiConsole.Status().StartAsync("Reading document...", async ctx => {
        if (docPath.EndsWith(".docx")) rawText = ExtractTextFromDocx(docPath);
        else if (docPath.EndsWith(".pdf")) rawText = ExtractTextFromPdf(docPath);
        else rawText = await File.ReadAllTextAsync(docPath);
    });

    var googleClient = new Client(apiKey: apiKey, httpOptions: new HttpOptions { ApiVersion = "v1beta" });
    IChatClient chatClient = googleClient.AsIChatClient(modelId);

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.BouncingBar)
        .StartAsync("Processing scope and building blueprint...", async ctx => 
        {
            try {
                // Pass 1: Condense
                ctx.Status("Analyzing project scope...");
                string condenserPrompt = "You are a Systems Engineer. Extract technical requirements (Hardware, Logic, Connections, Network) into a concise Markdown spec.";
                var condenseResponse = await chatClient.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, $"INSTRUCTIONS:\n{condenserPrompt}\n\nDOCUMENT:\n{rawText}") });
                string condensedSpec = condenseResponse.Text?.Trim() ?? "";

                // Pass 2: Build
                ctx.Status("Generating Fusion Blueprint...");
                string customerName = Path.GetFileNameWithoutExtension(docPath).Replace("_scope", "").Replace("-scope", "");
                string buildPrompt = GetSystemPrompt(customerName);
                string hints = LoadHints();
                var buildResponse = await chatClient.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, $"INSTRUCTIONS:\n{buildPrompt}\n\nHINTS:\n{hints}\n\nSPEC:\n{condensedSpec}") });
                
                string jsonResult = buildResponse.Text?.Trim() ?? "";
                if (jsonResult.Contains("```json")) jsonResult = jsonResult.Split("```json")[1].Split("```")[0].Trim();
                else if (jsonResult.StartsWith("```")) jsonResult = jsonResult.Replace("```", "").Trim();

                string fileName = $"fbp_{customerName.Replace(" ", "_")}.json";
                await File.WriteAllTextAsync(fileName, jsonResult);
                
                ctx.Status("Estimating Commission Time...");
                var estResponse = await chatClient.GetResponseAsync(new List<ChatMessage> 
                { 
                    new(ChatRole.User, $"Based on the complexity, hardware, and logic in this Fortna Fusion configuration, give a 1 to 2 sentence estimate of how long it will take to commission (install, configure, test, validate) this system on site. Just give the estimate directly.\n\nCONFIG:\n{jsonResult}") 
                });
                string estimate = estResponse.Text?.Trim() ?? "Estimate unavailable.";

                AnsiConsole.MarkupLine($"\n[green]SUCCESS! Generated Blueprint: {fileName}[/]");
                AnsiConsole.MarkupLine($"[cyan]Estimated Time to Commission: {estimate}[/]\n");
                StartFusionConsole(fileName);
            }
            catch (Exception ex) { AnsiConsole.WriteException(ex); }
        });
}

void StartFusionConsole(string blueprintPath)
{
    if (AnsiConsole.Confirm($"[yellow]Start Fortna Fusion with {blueprintPath}?[/]", true))
    {
        AnsiConsole.MarkupLine("[green]Launching Fortna Fusion...[/]");
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project ../DeviceSpace.Console/Fusion.Console.csproj -- {blueprintPath}",
                UseShellExecute = true,
                CreateNoWindow = false
            };
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex) { AnsiConsole.WriteException(ex); }
    }
}

string ExtractTextFromDocx(string path)
{
    using WordprocessingDocument wordDocument = WordprocessingDocument.Open(path, false);
    var body = wordDocument.MainDocumentPart?.Document.Body;
    return body?.InnerText ?? "";
}

string ExtractTextFromPdf(string path)
{
    var sb = new StringBuilder();
    using (var document = PdfDocument.Open(path))
    {
        foreach (var page in document.GetPages()) sb.AppendLine(page.Text);
    }
    return sb.ToString();
}

string GetSystemPrompt(string customerName)
{
    return $$"""
    You are a configuration expert for the Fortna Fusion automation system. 
    Generate a valid JSON "Fusion Blueprint" for: {{customerName}}.

    STRUCTURE:
    - Root: "AppSettings" -> "Fusion"
    - "Fusion": { "Name", "ColorConsole": true, "IsTestEnvironment": false, "Cores": [ { "Name": "Core1", "Elements": [], "Reactions": [] } ] }

    MANAGERS:
    [ActiveMqManager, ActiveMqBrowserManager, ActiveMqQueuePeekManager, AiElementManager, ApiDeviceManager, DatabaseElementManager, RedisCacheManager, TelemetryManager, InventoryManager, FireLogManager, FileMessageElementManager, TcpMessageClientElementManager, TcpMessageServerElementManager, MqttManager, NatsManager, EmailManager, PlcElementManager, AbCipPlcManager, VirtualPlcManager, PrinterManager, PrintClientManager, ScannerSimulatorManager, ScannerClientManager, DiagnosticElementManager, BlueprintVerifierManager]

    REACTIONS:
    [ReactionSimulation, PrintAndApplyFrc, StandardSort, HostOutputReaction]

    BOND OBJECT:
    { "Mode": 1, "Name": "BondName", "Source": "Topic", "Destination": "Topic", "Handler": "Method" }

    OUTPUT:
    Return ONLY raw JSON. No explanations.
    """;
}

string LoadHints()
{
    string hintsPath = Path.Combine(Directory.GetCurrentDirectory(), "hints.txt");
    return File.Exists(hintsPath) ? File.ReadAllText(hintsPath) : "";
}

async Task RunProvisionMode()
{
    var fusionFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "fbp_*.json");
    if (fusionFiles.Length == 0) { AnsiConsole.MarkupLine("[red]No blueprints found.[/]"); return; }

    var selectedFile = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[cyan]Select a Blueprint to Provision:[/]")
            .AddChoices(fusionFiles.Select(f => Path.GetFileName(f) ?? "")));

    string filePath = Path.Combine(Directory.GetCurrentDirectory(), selectedFile);
    var json = await File.ReadAllTextAsync(filePath);
    var root = JsonNode.Parse(json);
    var cores = root?["AppSettings"]?["Fusion"]?["Cores"]?.AsArray();
    
    List<JsonNode?> elements = cores != null 
        ? cores.SelectMany(c => c?["Elements"]?.AsArray() ?? new JsonArray()).ToList()
        : root?["AppSettings"]?["Fusion"]?["Elements"]?.AsArray().ToList() ?? new List<JsonNode?>();

    var printers = elements.Where(e => e?["Manager"]?.GetValue<string>().Contains("Printer") == true 
                                     || e?["Manager"]?.GetValue<string>().Contains("PrintClient") == true).ToList();

    if (!printers.Any()) { AnsiConsole.MarkupLine("[yellow]No printers found to provision.[/]"); return; }

    string subnet = AnsiConsole.Ask<string>("[yellow]Enter Subnet to scan (e.g., 10.33.218.0):[/] ");
    string baseIp = string.Join(".", subnet.Split('.').Take(3)) + ".";

    var foundIps = new List<string>();
    await AnsiConsole.Status().StartAsync($"Scanning {baseIp}1-254...", async ctx => {
        using var semaphore = new SemaphoreSlim(50);
        var tasks = Enumerable.Range(1, 254).Select(async i => {
            await semaphore.WaitAsync();
            try {
                using var client = new TcpClient();
                var res = client.BeginConnect(baseIp + i, 9100, null, null);
                if (res.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(200))) {
                    lock (foundIps) foundIps.Add(baseIp + i);
                    AnsiConsole.MarkupLine($"  [green][FOUND][/] {baseIp + i}");
                }
            } finally { semaphore.Release(); }
        });
        await Task.WhenAll(tasks);
    });

    if (!foundIps.Any()) return;

    for (int i = 0; i < Math.Min(printers.Count, foundIps.Count); i++) {
        var props = printers[i]?["Properties"]?.AsObject();
        if (props != null) props["IPAddress"] = foundIps[i];
    }

    await File.WriteAllTextAsync(filePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    AnsiConsole.MarkupLine("[green]Blueprint updated![/]");
}
