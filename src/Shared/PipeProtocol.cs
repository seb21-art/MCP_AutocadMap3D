using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpMap3D.Shared;

/// <summary>
/// Contrat d'échange entre le serveur MCP et le plug-in AutoCAD :
/// une requête JSON par ligne (UTF-8, terminée par '\n'), une réponse JSON par ligne.
/// Ce fichier est compilé dans les deux projets.
/// </summary>
public static class PipeProtocol
{
    public const int Version = 1;

    /// <summary>
    /// Nom du pipe, propre à la session Windows : les pipes nommés sont globaux à la machine,
    /// ce suffixe évite toute collision entre utilisateurs connectés simultanément.
    /// </summary>
    public static string PipeName { get; } =
        $"McpMap3D.v{Version}.s{Process.GetCurrentProcess().SessionId}";

    /// <summary>
    /// Accents et symboles non échappés (lisibilité, taille) ; les caractères de contrôle le restent,
    /// ce qui garantit qu'un message tient sur une ligne.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public sealed class PipeRequest
{
    public string? Id { get; set; }

    public string Method { get; set; } = "";

    public JsonElement? Params { get; set; }

    /// <summary>Délai maximal d'attente de disponibilité d'AutoCAD, en millisecondes.</summary>
    public int? TimeoutMs { get; set; }
}

public sealed class PipeResponse
{
    public string? Id { get; set; }

    public bool Ok { get; set; }

    public JsonElement? Result { get; set; }

    public PipeError? Error { get; set; }

    public static PipeResponse Success(string? id, JsonElement? result) =>
        new() { Id = id, Ok = true, Result = result };

    public static PipeResponse Failure(string? id, string code, string message) =>
        new() { Id = id, Ok = false, Error = new PipeError { Code = code, Message = message } };
}

public sealed class PipeError
{
    public string Code { get; set; } = "";

    public string Message { get; set; } = "";
}

public static class PipeErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string UnknownMethod = "unknown_method";
    public const string InvalidParams = "invalid_params";
    public const string Busy = "autocad_busy";
    public const string NoDocument = "no_document";
    public const string MapUnavailable = "map_unavailable";
    public const string ShuttingDown = "shutting_down";
    public const string Internal = "internal_error";
}

/// <summary>Erreur métier renvoyée telle quelle au client.</summary>
public sealed class PipeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
