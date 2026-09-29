using System.Reflection;
using McpMap3D.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

const string ServerName = "map3d";
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

// Sans argument : serveur MCP sur stdio. Avec argument : commandes d'installation.
if (args.Length > 0)
{
    return args[0] switch
    {
        "register-desktop" => ClaudeDesktopConfig.Register(ServerName, Environment.ProcessPath!),
        "unregister-desktop" => ClaudeDesktopConfig.Unregister(ServerName),
        "--version" => PrintVersion(version),
        _ => PrintUsage(),
    };
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
});

// stdout est réservé au protocole MCP : tous les journaux partent sur stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);

builder.Services
    .AddSingleton<PluginClient>()
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "mcp-map3d", Title = "AutoCAD Map 3D", Version = version };
        options.ServerInstructions = McpMap3D.Server.Tools.ToolHelp.Instructions;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// Schémas allégés une fois pour toutes, après l'enregistrement des outils (voir ToolSchemaCompactor).
builder.Services.PostConfigure<McpServerOptions>(options =>
{
    foreach (var tool in options.ToolCollection ?? [])
        tool.ProtocolTool.InputSchema = ToolSchemaCompactor.Compact(tool.ProtocolTool.InputSchema);
});

await builder.Build().RunAsync();
return 0;

static int PrintVersion(string version)
{
    Console.WriteLine(version);
    return 0;
}

static int PrintUsage()
{
    Console.Error.WriteLine("""
        Usage : McpMap3D.Server [commande]
          (aucune)             démarre le serveur MCP sur stdio
          register-desktop     enregistre ce serveur dans la configuration de Claude Desktop
          unregister-desktop   retire ce serveur de la configuration de Claude Desktop
          --version            affiche la version
        """);
    return 2;
}
