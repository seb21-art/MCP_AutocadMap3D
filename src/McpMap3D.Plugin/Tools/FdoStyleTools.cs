using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Autodesk.Gis.Map.Platform;
using McpMap3D.Shared;
using OSGeo.MapGuide;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Style d'un calque FDO vectoriel : couleurs, épaisseur, transparence, analyse thématique (une règle par valeur
/// ou par plage d'une propriété) et étiquettes. La définition de calque est réécrite (copiée dans Library://MCP_…
/// si elle n'a pas été créée par le connecteur), puis appliquée au calque. N'est pas annulé par U.
/// </summary>
internal static class FdoStyleTools
{
    private const int MaxAutoValues = 30;

    /// <summary>Palette des valeurs automatiques : couleurs nettement distinctes.</summary>
    private static readonly string[] Palette =
    [
        "1F77B4", "FF7F0E", "2CA02C", "D62728", "9467BD", "8C564B", "E377C2", "7F7F7F", "BCBD22", "17BECF",
        "AEC7E8", "FFBB78", "98DF8A", "FF9896", "C5B0D5", "C49C94", "F7B6D2", "C7C7C7", "DBDB8D", "9EDAE5",
    ];

    private sealed record Rule(string Label, string? Filter, string Color);

    public static object SetLayerStyle(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        return FdoTools.Map(() =>
        {
            var layer = FdoTools.RequireLayer(reader.RequireString("name"));
            var resources = FdoTools.ResourceService();
            var currentId = layer.GetLayerDefinition();
            var current = FdoTools.Xml(FdoTools.Read(resources.GetResourceContent(currentId)));
            var vector = current.Descendants().FirstOrDefault(e => e.Name.LocalName == "VectorLayerDefinition")
                ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"« {layer.GetName()} » n'est pas un calque vectoriel : le style d'une image (WMS, raster) ne se règle pas ici.");
            string Child(string name) => vector.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value
                ?? throw new PipeException(PipeErrorCodes.Internal, $"Définition de calque sans {name}.");
            var (source, featureName, geometry) = (Child("ResourceId"), Child("FeatureName"), Child("Geometry"));

            var definition = FdoTools.ClassDefinition(FdoTools.Features(), new MgResourceIdentifier(source),
                featureName.Split(':')[0], featureName.Contains(':') ? featureName.Split(':', 2)[1] : featureName);
            var types = (FdoTools.AllProperties(definition).OfType<MgGeometricPropertyDefinition>()
                .FirstOrDefault(p => p.GetName() == geometry)?.GetGeometryTypes()) ?? 4;
            var kind = (types & 4) != 0 ? "area" : (types & 2) != 0 ? "line" : "point";

            var color = Hex(reader.GetString("color") ?? (kind == "line" ? "0,51,153" : "0,122,204"));
            var outline = Hex(reader.GetString("outlineColor") ?? "0,51,102");
            var transparency = reader.GetInt("transparency", kind == "area" ? 60 : 0, 0, 100);
            var weight = reader.GetDouble("lineWeight", 0.25);
            var pointSize = reader.GetDouble("pointSize", 6);
            var rules = ThemeRules(reader, source, featureName, definition, color);
            var label = LabelXml(reader, definition);

            var body = new StringBuilder();
            body.Append(kind switch { "area" => "<AreaTypeStyle>", "line" => "<LineTypeStyle>", _ => "<PointTypeStyle>" });
            foreach (var rule in rules)
            {
                var filter = rule.Filter is null ? "" : $"<Filter>{FdoTools.Escape(rule.Filter)}</Filter>";
                var legend = $"<LegendLabel>{FdoTools.Escape(rule.Label)}</LegendLabel>";
                body.Append(kind switch
                {
                    "area" => $"<AreaRule>{legend}{filter}{label}<AreaSymbolization2D><Fill><FillPattern>Solid</FillPattern>" +
                              $"<ForegroundColor>{Alpha(transparency)}{rule.Color}</ForegroundColor><BackgroundColor>{Alpha(transparency)}{rule.Color}</BackgroundColor></Fill>" +
                              $"<Stroke><LineStyle>Solid</LineStyle><Thickness>{Number(weight)}</Thickness><Color>FF{outline}</Color><Unit>Millimeters</Unit></Stroke>" +
                              "</AreaSymbolization2D></AreaRule>",
                    "line" => $"<LineRule>{legend}{filter}{label}<LineSymbolization2D><LineStyle>Solid</LineStyle><Thickness>{Number(weight)}</Thickness>" +
                              $"<Color>FF{rule.Color}</Color><Unit>Millimeters</Unit></LineSymbolization2D></LineRule>",
                    _ => $"<PointRule>{legend}{filter}{label}<PointSymbolization2D><Mark><Unit>Points</Unit><SizeContext>DeviceUnits</SizeContext>" +
                         $"<SizeX>{Number(pointSize)}</SizeX><SizeY>{Number(pointSize)}</SizeY><Shape>Circle</Shape>" +
                         $"<Fill><FillPattern>Solid</FillPattern><ForegroundColor>FF{rule.Color}</ForegroundColor><BackgroundColor>FF{rule.Color}</BackgroundColor></Fill>" +
                         $"<Edge><LineStyle>Solid</LineStyle><Thickness>1</Thickness><Color>FF{outline}</Color><Unit>Points</Unit></Edge></Mark></PointSymbolization2D></PointRule>",
                });
            }

            body.Append(kind switch { "area" => "</AreaTypeStyle>", "line" => "</LineTypeStyle>", _ => "</PointTypeStyle>" });
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                + "<LayerDefinition xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"LayerDefinition-2.4.0.xsd\" version=\"2.4.0\">"
                + "<VectorLayerDefinition><ResourceId>" + FdoTools.Escape(source) + "</ResourceId><FeatureName>" + FdoTools.Escape(featureName)
                + "</FeatureName><FeatureNameType>FeatureClass</FeatureNameType><Geometry>" + FdoTools.Escape(geometry)
                + "</Geometry><VectorScaleRange>" + body + "</VectorScaleRange></VectorLayerDefinition></LayerDefinition>";

            // Définition propre au connecteur : réécrite sur place ; sinon copiée, pour ne pas toucher à celle de
            // MAPCONNECT ou d'un autre calque.
            var target = currentId.ToString().StartsWith(FdoTools.OwnedPrefix, StringComparison.OrdinalIgnoreCase)
                ? currentId
                : new MgResourceIdentifier($"{FdoTools.OwnedPrefix}{FdoTools.SafeName(layer.GetName())}_style.LayerDefinition");
            FdoTools.Save(resources, target, xml);
            layer.SetLayerDefinition(target, resources);
            layer.ForceRefresh();
            context.AfterCommit(() => context.Editor.Regen());
            return new
            {
                Layer = layer.GetName(),
                LayerDefinition = target.ToString(),
                Geometry = kind,
                Rules = rules.Select(rule => new { rule.Label, Filter = rule.Filter, Color = "#" + rule.Color }).ToList(),
                Labels = label.Length > 0 ? reader.GetString("labelProperty") : null,
            };
        });
    }

    /// <summary>
    /// Règles de l'analyse thématique : valeurs listées (themeValues), plages (themeRanges), ou valeurs distinctes
    /// trouvées dans la source (themeProperty seul), plus une règle « Autres » ; une seule règle sans thème.
    /// </summary>
    private static List<Rule> ThemeRules(ArgReader reader, string source, string featureName, MgClassDefinition definition, string color)
    {
        var property = reader.GetString("themeProperty");
        if (property is null)
        {
            return reader.Has("themeValues") || reader.Has("themeRanges")
                ? throw new PipeException(PipeErrorCodes.InvalidParams, "themeValues et themeRanges demandent themeProperty.")
                : [new Rule("", null, color)];
        }

        var data = FdoTools.AllProperties(definition).OfType<MgDataPropertyDefinition>()
            .FirstOrDefault(p => string.Equals(p.GetName(), property, StringComparison.OrdinalIgnoreCase))
            ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Propriété « {property} » inconnue : {string.Join(", ", FdoTools.AllProperties(definition).OfType<MgDataPropertyDefinition>().Select(p => p.GetName()))}.");
        var name = Identifier(data.GetName());
        var textual = data.GetDataType() is MgPropertyType.String or MgPropertyType.Clob;
        var rules = new List<Rule>();

        if (reader.TryGet("themeRanges", out var ranges))
        {
            var index = 0;
            foreach (var range in Items(ranges, "themeRanges"))
            {
                var item = new ArgReader(range);
                var (min, max) = (item.Has("min") ? item.GetDouble("min", 0) : (double?)null, item.Has("max") ? item.GetDouble("max", 0) : (double?)null);
                if (min is null && max is null)
                    throw new PipeException(PipeErrorCodes.InvalidParams, "Chaque plage de themeRanges demande min, max ou les deux.");
                var filter = string.Join(" AND ", new[]
                {
                    min is { } low ? $"{name} >= {Number(low)}" : null,
                    max is { } high ? $"{name} < {Number(high)}" : null,
                }.OfType<string>());
                var label = item.GetString("label") ?? $"{(min is { } a ? Number(a) : "…")} à {(max is { } b ? Number(b) : "…")}";
                rules.Add(new Rule(label, filter, Hex(item.GetString("color") ?? Palette[index++ % Palette.Length])));
            }
        }
        else
        {
            var values = reader.TryGet("themeValues", out var given)
                ? Items(given, "themeValues").Select(element => new ArgReader(element)).Select(item => (
                    Value: item.TryGet("value", out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText()) : throw new PipeException(PipeErrorCodes.InvalidParams, "Chaque valeur de themeValues demande value."),
                    Color: item.GetString("color"),
                    Label: item.GetString("label"))).ToList()
                : DistinctValues(source, featureName, data.GetName()).Select(value => (Value: value, Color: (string?)null, Label: (string?)null)).ToList();
            if (values.Count == 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"La propriété « {property} » n'a aucune valeur dans la source.");

            for (var index = 0; index < values.Count; index++)
            {
                var (value, ruleColor, label) = values[index];
                var literal = textual ? $"'{value.Replace("'", "''")}'" : value;
                rules.Add(new Rule(label ?? value, $"{name} = {literal}", Hex(ruleColor ?? Palette[index % Palette.Length])));
            }
        }

        rules.Add(new Rule("Autres", null, Hex(reader.GetString("otherColor") ?? "160,160,160")));
        return rules;
    }

    /// <summary>Valeurs distinctes d'une propriété, triées, 30 au plus.</summary>
    private static List<string> DistinctValues(string source, string featureName, string property)
    {
        var options = new MgFeatureQueryOptions();
        options.AddFeatureProperty(property);
        var featureReader = FdoTools.Features().SelectFeatures(new MgResourceIdentifier(source), featureName, options);
        var values = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (featureReader.ReadNext())
            {
                if (featureReader.IsNull(property))
                    continue;
                var value = featureReader.GetPropertyType(property) switch
                {
                    MgPropertyType.String => featureReader.GetString(property),
                    MgPropertyType.Int16 => featureReader.GetInt16(property).ToString(CultureInfo.InvariantCulture),
                    MgPropertyType.Int32 => featureReader.GetInt32(property).ToString(CultureInfo.InvariantCulture),
                    MgPropertyType.Int64 => featureReader.GetInt64(property).ToString(CultureInfo.InvariantCulture),
                    MgPropertyType.Double => featureReader.GetDouble(property).ToString("R", CultureInfo.InvariantCulture),
                    MgPropertyType.Single => featureReader.GetSingle(property).ToString("R", CultureInfo.InvariantCulture),
                    MgPropertyType.Boolean => featureReader.GetBoolean(property) ? "1" : "0",
                    _ => null,
                };
                if (value is not null && values.Add(value) && values.Count > MaxAutoValues)
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"Plus de {MaxAutoValues} valeurs distinctes : donnez themeValues (valeurs choisies) ou themeRanges (plages).");
            }
        }
        finally
        {
            featureReader.Close();
        }

        return [.. values];
    }

    /// <summary>Étiquette TextSymbol de chaque règle : propriété affichée, hauteur en points, couleur.</summary>
    private static string LabelXml(ArgReader reader, MgClassDefinition definition)
    {
        if (reader.GetString("labelProperty") is not { } property)
            return "";
        var data = FdoTools.AllProperties(definition).OfType<MgDataPropertyDefinition>()
            .FirstOrDefault(p => string.Equals(p.GetName(), property, StringComparison.OrdinalIgnoreCase))
            ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"Propriété d'étiquette « {property} » inconnue.");
        var height = Number(reader.GetDouble("labelHeight", 9));
        var color = Hex(reader.GetString("labelColor") ?? "0,0,0");
        return $"<Label><Unit>Points</Unit><SizeContext>DeviceUnits</SizeContext><SizeX>{height}</SizeX><SizeY>{height}</SizeY>" +
               $"<Rotation>0</Rotation><Text>{FdoTools.Escape(Identifier(data.GetName()))}</Text><FontName>Arial</FontName>" +
               $"<ForegroundColor>FF{color}</ForegroundColor><BackgroundColor>FFFFFFFF</BackgroundColor><BackgroundStyle>Transparent</BackgroundStyle>" +
               "<HorizontalAlignment>'Center'</HorizontalAlignment><VerticalAlignment>'Baseline'</VerticalAlignment></Label>";
    }

    private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0
            ? element.EnumerateArray()
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » attend une liste non vide.");

    /// <summary>Nom de propriété dans une expression FDO : entre guillemets s'il contient autre chose que lettres, chiffres et _.</summary>
    private static string Identifier(string name) =>
        name.All(c => char.IsLetterOrDigit(c) || c == '_') ? name : $"\"{name.Replace("\"", "\"\"")}\"";

    /// <summary>Couleur RRGGBB : « R,G,B », #RRGGBB, nom ou index AutoCAD.</summary>
    private static string Hex(string text)
    {
        var trimmed = text.Trim().TrimStart('#');
        if (trimmed.Length == 6 && trimmed.All(char.IsAsciiHexDigit))
            return trimmed.ToUpperInvariant();
        var color = EntityProperties.ParseColor(trimmed).ColorValue;
        return $"{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>Canal alpha d'un remplissage : 0 % de transparence = FF.</summary>
    private static string Alpha(int transparency) => ((int)Math.Round(255 * (100 - transparency) / 100.0)).ToString("X2");

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
