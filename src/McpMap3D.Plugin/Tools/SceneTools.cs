using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;
using AttenuationType = Autodesk.AutoCAD.GraphicsInterface.AttenuationType;
using DrawableType = Autodesk.AutoCAD.GraphicsInterface.DrawableType;
using GiChannel = Autodesk.AutoCAD.GraphicsInterface.ChannelFlags;
using GiMethod = Autodesk.AutoCAD.GraphicsInterface.Method;
using GiMode = Autodesk.AutoCAD.GraphicsInterface.Mode;
using GiSource = Autodesk.AutoCAD.GraphicsInterface.Source;
using ImageFileTexture = Autodesk.AutoCAD.GraphicsInterface.ImageFileTexture;
using MaterialColor = Autodesk.AutoCAD.GraphicsInterface.MaterialColor;
using MaterialDiffuse = Autodesk.AutoCAD.GraphicsInterface.MaterialDiffuseComponent;
using MaterialMap = Autodesk.AutoCAD.GraphicsInterface.MaterialMap;
using MaterialOpacity = Autodesk.AutoCAD.GraphicsInterface.MaterialOpacityComponent;
using MaterialSpecular = Autodesk.AutoCAD.GraphicsInterface.MaterialSpecularComponent;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Matériaux, lumières, soleil calé sur le système de coordonnées du dessin, et ambiance de la vue
/// (arrière-plan, luminosité, éclairage par défaut). Les angles échangés sont en degrés.
/// </summary>
internal static class SceneTools
{
    private const string ByLayer = "ByLayer";
    private const string ByBlock = "ByBlock";

    public static object ListMaterials(ToolContext context, JsonElement? args)
    {
        var filter = NameFilter.Create(new ArgReader(args).GetStrings("names"));
        var transaction = context.RequireTransaction();
        var dictionary = (DBDictionary)transaction.GetObject(context.Database.MaterialDictionaryId, OpenMode.ForRead);
        var materials = new List<object>();
        foreach (DBDictionaryEntry entry in dictionary)
        {
            if (transaction.GetObject(entry.Value, OpenMode.ForRead) is not Material material)
                continue;
            if (filter is null || filter.IsMatch(material.Name))
                materials.Add(DescribeMaterial(material));
        }

        return new { Count = materials.Count, Materials = materials };
    }

    public static object SetMaterial(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var name = RequireMaterialName(reader.RequireString("name"));
        var dictionary = (DBDictionary)transaction.GetObject(context.Database.MaterialDictionaryId, OpenMode.ForRead);

        if (IsSpecial(name))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"« {name} » est un matériau réservé d'AutoCAD : choisissez un autre nom.");

        var created = FindEntry(dictionary, name) is null;
        Material material;
        if (created)
        {
            var sourceName = reader.GetString("basedOn");
            if (sourceName is not null)
            {
                var sourceId = RequireMaterial(transaction, dictionary, sourceName);
                var source = (Material)transaction.GetObject(sourceId, OpenMode.ForRead);
                material = (Material)source.Clone();
                material.Name = name;
            }
            else
            {
                material = new Material { Name = name };
            }

            dictionary.UpgradeOpen();
            dictionary.SetAt(name, material);
            transaction.AddNewlyCreatedDBObject(material, true);
        }
        else
        {
            if (reader.Has("basedOn"))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Le matériau « {name} » existe déjà : basedOn ne sert qu'à la création.");

            material = (Material)transaction.GetObject(FindEntry(dictionary, name)!.Value, OpenMode.ForWrite);
        }

        if (reader.Has("description"))
            material.Description = reader.GetString("description") ?? "";

        if (reader.Has("color") || reader.Has("texture") || created && !reader.Has("basedOn"))
            ApplyDiffuse(material, reader);

        if (reader.Has("opacity"))
        {
            var opacity = reader.RequireDouble("opacity");
            if (opacity is < 0 or > 1)
                throw new PipeException(PipeErrorCodes.InvalidParams, "L'opacité doit être comprise entre 0 et 1 (1 = opaque).");

            material.Opacity = new MaterialOpacity(opacity, material.Opacity.Map);
        }

        if (reader.Has("shininess"))
        {
            var shininess = reader.RequireDouble("shininess");
            if (shininess is < 0 or > 1)
                throw new PipeException(PipeErrorCodes.InvalidParams, "La brillance doit être comprise entre 0 et 1.");

            var specular = material.Specular;
            material.Specular = new MaterialSpecular(specular.Color, specular.Map, shininess);
        }

        if (reader.Has("reflectivity"))
        {
            var reflectivity = reader.RequireDouble("reflectivity");
            material.Reflectivity = reflectivity is >= 0 and <= 1
                ? reflectivity
                : throw new PipeException(PipeErrorCodes.InvalidParams, "La réflexion doit être comprise entre 0 et 1.");
        }

        if (reader.Has("twoSided"))
            material.TwoSided = reader.GetBool("twoSided", false);

        if (created && !reader.Has("basedOn"))
        {
            material.Mode = GiMode.Realistic;
            material.ChannelFlags = GiChannel.UseAll;
        }

        context.AfterCommit(() => context.Editor.Regen());
        return new { Created = created, Material = DescribeMaterial(material) };
    }

    public static object AssignMaterial(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var requested = reader.RequireString("material").Trim();
        var dictionary = (DBDictionary)transaction.GetObject(database.MaterialDictionaryId, OpenMode.ForRead);
        var special = IsSpecial(requested);
        var materialId = special ? ObjectId.Null : RequireMaterial(transaction, dictionary, requested);
        var materialName = special ? CanonicalSpecial(requested) : ((Material)transaction.GetObject(materialId, OpenMode.ForRead)).Name;

        var objects = new List<string>();
        if (reader.Has("handles"))
        {
            foreach (var id in Handles.Resolve(context, reader))
            {
                var entity = (Entity)transaction.GetObject(id, OpenMode.ForWrite);
                EnsureWritable(transaction, entity);
                entity.Material = materialName;
                objects.Add(entity.Handle.ToString());
            }
        }

        var layers = new List<string>();
        foreach (var layerName in reader.GetStrings("layers"))
        {
            if (special)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    "Un calque reçoit un matériau du dessin, pas ByLayer ni ByBlock.");

            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (!table.Has(layerName))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Le calque « {layerName} » n'existe pas.");

            var layer = (LayerTableRecord)transaction.GetObject(table[layerName], OpenMode.ForWrite);
            layer.MaterialId = materialId;
            layers.Add(layer.Name);
        }

        object? face = null;
        if (reader.Has("handle") || reader.Has("face"))
        {
            if (!reader.Has("handle") || !reader.Has("face"))
                throw new PipeException(PipeErrorCodes.InvalidParams, "Une face se désigne par handle et face (indice de get_solid_topology).");

            if (string.Equals(materialName, ByBlock, StringComparison.OrdinalIgnoreCase))
                throw new PipeException(PipeErrorCodes.InvalidParams, "ByBlock ne s'applique pas à une face : utilisez un matériau ou ByLayer.");

            var solidId = Handles.Resolve(context, reader, "handle")[0];
            if (transaction.GetObject(solidId, OpenMode.ForWrite) is not Solid3d solid)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {solidId.Handle} n'est pas un solide 3D.");

            EnsureWritable(transaction, solid);
            var index = reader.GetInt("face", -1, min: 0, max: int.MaxValue);
            var subentity = ModelingTools.RequireFace(solid, index);
            try
            {
                solid.SetSubentityMaterial(subentity, string.Equals(materialName, ByLayer, StringComparison.OrdinalIgnoreCase) ? ObjectId.Null : materialId);
            }
            catch (AcException ex)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Impossible d'affecter le matériau à la face {index} : {ex.Message}");
            }

            face = new { Handle = solid.Handle.ToString(), Index = index, Material = materialName };
        }

        if (objects.Count == 0 && layers.Count == 0 && face is null)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez handles, layers, ou handle et face.");

        context.AfterCommit(() => context.Editor.Regen());
        return new { Material = materialName, Objects = objects, Layers = layers, Face = face };
    }

    public static object ListLights(ToolContext context, JsonElement? args)
    {
        var filter = NameFilter.Create(new ArgReader(args).GetStrings("names"));
        var lights = CollectLights(context)
            .Where(light => filter is null || filter.IsMatch(light.Name))
            .Select(DescribeLight)
            .ToList();
        return new { Count = lights.Count, LightingUnits = LightingUnitsName(context.Database), Lights = lights };
    }

    public static object SetLight(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        Light light;
        var created = !reader.Has("handle");
        if (created)
        {
            var type = ParseLightType(reader.RequireString("type"));
            light = new Light { LightType = type, IsOn = true };
            light.Name = reader.Has("name") ? RequireLightName(reader.RequireString("name")) : NextLightName(context);
            if (CollectLights(context).Any(existing => string.Equals(existing.Name, light.Name, StringComparison.OrdinalIgnoreCase)))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Une lumière « {light.Name} » existe déjà.");

            // Ajoutée au dessin avant d'être réglée : plusieurs propriétés d'une lumière (intensité photométrique
            // notamment) lèvent eNoDatabase sur un objet qui n'est pas encore dans le dessin. Une erreur de paramètre
            // levée ensuite annule la transaction, donc la lumière avec.
            AppendLight(context, light, reader.GetString("layer"));
            ApplyLight(context.Database, light, reader, creating: true);
        }
        else
        {
            var id = Handles.Resolve(context, reader, "handle")[0];
            if (transaction.GetObject(id, OpenMode.ForWrite) is not Light existing)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas une lumière.");

            EnsureWritable(transaction, existing);
            light = existing;
            if (reader.Has("type"))
                light.LightType = ParseLightType(reader.RequireString("type"));
            if (reader.Has("name"))
            {
                var name = RequireLightName(reader.RequireString("name"));
                if (CollectLights(context).Any(other => other.ObjectId != light.ObjectId && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new PipeException(PipeErrorCodes.InvalidParams, $"Une lumière « {name} » existe déjà.");
                light.Name = name;
            }

            ApplyLight(context.Database, light, reader, creating: false);
            if (light.LightType == DrawableType.WebLight && string.IsNullOrWhiteSpace(light.WebFile))
                throw new PipeException(PipeErrorCodes.InvalidParams, "Une lumière de type web demande webFile (fichier IES).");
            if (reader.Has("layer"))
                light.Layer = RequireLayer(context, reader.GetString("layer")!);
        }

        context.AfterCommit(() => context.Editor.Regen());
        return new { Created = created, Light = DescribeLight(light), LightingUnits = LightingUnitsName(context.Database) };
    }

    public static object DeleteLights(ToolContext context, JsonElement? args)
    {
        var transaction = context.RequireTransaction();
        var erased = new List<string>();
        foreach (var id in Handles.Resolve(context, new ArgReader(args)))
        {
            if (transaction.GetObject(id, OpenMode.ForWrite) is not Light light)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas une lumière.");

            EnsureWritable(transaction, light);
            var handle = light.Handle.ToString();
            light.Erase();
            erased.Add(handle);
        }

        context.AfterCommit(() => context.Editor.Regen());
        return new { Erased = erased.Count, Handles = erased };
    }

    public static object GetSun(ToolContext context, JsonElement? args)
    {
        var (view, kind) = OpenView(context, new ArgReader(args), OpenMode.ForRead);
        return DescribeSun(context, view, kind, calibratedNow: null, warning: null);
    }

    public static object SetSun(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var (view, kind) = OpenView(context, reader, OpenMode.ForWrite);
        var sun = EnsureSun(context.RequireTransaction(), view);

        if (reader.Has("on"))
            sun.IsOn = reader.GetBool("on", true);
        else if (sun.DateTime == default)
            sun.IsOn = true;

        var dateChanged = reader.Has("date") || reader.Has("time") || reader.Has("dateTime");
        if (dateChanged)
        {
            try
            {
                sun.DateTime = SunCalibration.ParseLocal(
                    reader.GetString("date"),
                    reader.GetString("time"),
                    reader.GetString("dateTime"),
                    sun.DateTime == default ? DateTime.Today : sun.DateTime);
            }
            catch (ArgumentException ex)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams, ex.Message);
            }
        }

        if (reader.Has("shadows"))
        {
            var shadows = sun.ShadowParameters;
            shadows.ShadowsOn = reader.GetBool("shadows", true);
            sun.ShadowParameters = shadows;
        }

        if (reader.Has("intensity"))
        {
            var intensity = reader.RequireDouble("intensity");
            sun.Intensity = intensity >= 0
                ? intensity
                : throw new PipeException(PipeErrorCodes.InvalidParams, "L'intensité du soleil doit être positive ou nulle.");
        }

        if (reader.Has("color"))
            sun.SunColor = EditTools.ParseColor(reader.RequireString("color"));

        var place = ApplyPlace(context, reader, sun, dateChanged);
        RefreshAfterCommit(context, view);
        var described = DescribeSun(context, view, kind, place.Calibrated ? true : null, place.Warning, place.Anchor);

        // AutoCAD ne recalcule l'azimut et la hauteur du soleil qu'à la validation de la transaction (constaté dans
        // Civil 3D 2026) : ils sont relus une fois la transaction validée.
        var sunId = sun.ObjectId;
        context.ResultAfterCommit = () =>
        {
            // La modification est déjà validée : un échec de relecture ne doit pas la faire passer pour un échec.
            var result = JsonSerializer.SerializeToNode(described, PipeProtocol.JsonOptions)!.AsObject();
            try
            {
                using var transaction = context.Database.TransactionManager.StartOpenCloseTransaction();
                var fresh = (Sun)transaction.GetObject(sunId, OpenMode.ForRead);
                result["azimuth"] = Format.Number(fresh.Azimuth);
                result["altitude"] = Format.Number(fresh.Altitude);
                result["direction"] = JsonSerializer.SerializeToNode(Format.Vector(fresh.SunDirection), PipeProtocol.JsonOptions);
                transaction.Commit();
            }
            catch (AcException ex)
            {
                Log.Error("Relecture du soleil après validation", ex);
                result["warning"] = string.Join(" ", new[] { (string?)result["warning"], "Azimut et hauteur non relus : relisez-les avec get_sun." }
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            }

            return result;
        };

        return described;
    }

    public static object GetViewAmbiance(ToolContext context, JsonElement? args)
    {
        var (view, kind) = OpenView(context, new ArgReader(args), OpenMode.ForRead);
        return DescribeAmbiance(context, view, kind);
    }

    public static object SetViewAmbiance(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        if (!reader.Has("background") && !reader.Has("brightness") && !reader.Has("contrast") && !reader.Has("defaultLighting")
            && !reader.Has("visualStyle"))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Indiquez background, brightness, contrast, defaultLighting ou visualStyle.");

        var transaction = context.RequireTransaction();
        var (view, kind) = OpenView(context, reader, OpenMode.ForWrite);
        if (reader.Has("background"))
            ApplyBackground(context, view, reader);

        if (reader.Has("brightness"))
            SetBrightness(view, reader.RequireDouble("brightness"));
        if (reader.Has("contrast"))
            SetContrast(view, reader.RequireDouble("contrast"));
        if (reader.Has("defaultLighting"))
            ApplyDefaultLighting(view, reader.RequireString("defaultLighting"));
        if (reader.Has("visualStyle"))
            SetVisualStyle(view, ModelingTools.FindVisualStyle(context.Database, transaction, reader.RequireString("visualStyle")));

        RefreshAfterCommit(context, view);
        return DescribeAmbiance(context, view, kind);
    }

    private static void ApplyDiffuse(Material material, ArgReader reader)
    {
        var current = material.Diffuse.Color;
        var color = reader.Has("color")
            ? ToEntityColor(EditTools.ParseColor(reader.RequireString("color")))
            : current.Color.ColorMethod == ColorMethod.ByColor || current.Method == GiMethod.Override
                ? current.Color
                : new EntityColor(200, 200, 200);
        var map = reader.Has("texture") ? TextureMap(reader.GetString("texture")) : material.Diffuse.Map;
        var diffuseColor = new MaterialColor(GiMethod.Override, 1, color);
        material.Diffuse = new MaterialDiffuse(diffuseColor, map);
        material.Ambient = diffuseColor;
    }

    private static MaterialMap TextureMap(string? texture)
    {
        if (string.IsNullOrWhiteSpace(texture))
            return new MaterialMap();

        var path = texture.Trim();
        if (!File.Exists(path))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Fichier de texture introuvable : {path}");

        return new MaterialMap(GiSource.File, new ImageFileTexture { SourceFileName = path }, 1.0, null);
    }

    private static object DescribeMaterial(Material material)
    {
        var diffuse = material.Diffuse;
        var texture = diffuse.Map.Texture is ImageFileTexture image && !string.IsNullOrWhiteSpace(image.SourceFileName)
            ? image.SourceFileName
            : null;
        return new
        {
            material.Name,
            Description = string.IsNullOrWhiteSpace(material.Description) ? null : material.Description,
            Anonymous = material.Anonymous ? true : (bool?)null,
            Color = DescribeEntityColor(diffuse.Color.Color),
            Opacity = Format.Number(material.Opacity.Percentage),
            Shininess = Format.Number(material.Specular.Gloss),
            Reflectivity = Format.Number(material.Reflectivity),
            material.TwoSided,
            Texture = texture,
        };
    }

    private static void ApplyLight(Database database, Light light, ArgReader reader, bool creating)
    {
        if (reader.Has("position"))
            light.Position = reader.RequirePoint("position");
        else if (creating && light.LightType is DrawableType.PointLight or DrawableType.SpotLight)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Une lumière ponctuelle ou un projecteur demande une position [x,y,z].");

        if (reader.Has("target"))
        {
            light.TargetLocation = reader.RequirePoint("target");
            light.HasTarget = true;
        }
        else if (reader.Has("direction"))
        {
            var direction = reader.RequirePoint("direction");
            var vector = new Vector3d(direction.X, direction.Y, direction.Z);
            if (vector.Length < 1e-9)
                throw new PipeException(PipeErrorCodes.InvalidParams, "La direction de la lumière est nulle.");
            light.Direction = vector.GetNormal();
            light.HasTarget = false;
        }
        else if (creating && light.LightType is DrawableType.SpotLight or DrawableType.DistantLight)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Un projecteur ou une lumière lointaine demande target ou direction.");

        // En unités photométriques, l'intensité est celle de la lampe (PhysicalIntensity, en candelas) et
        // Intensity n'est qu'un facteur multiplicateur, laissé à 1 à la création.
        if (reader.Has("intensity") || creating)
        {
            var intensity = reader.GetDouble("intensity", 1);
            if (intensity < 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "L'intensité doit être positive ou nulle.");
            if (IsPhotometric(database))
            {
                if (reader.Has("intensity"))
                    light.PhysicalIntensity = intensity;
                if (creating)
                    light.Intensity = 1;
            }
            else
            {
                light.Intensity = intensity;
            }
        }

        if (reader.Has("color"))
            light.LightColor = EditTools.ParseColor(reader.RequireString("color"));
        if (reader.Has("on"))
            light.IsOn = reader.GetBool("on", true);
        if (reader.Has("shadows"))
        {
            var shadow = light.Shadow;
            shadow.ShadowsOn = reader.GetBool("shadows", true);
            light.Shadow = shadow;
        }

        if (reader.Has("hotspot") || reader.Has("falloff"))
        {
            if (light.LightType != DrawableType.SpotLight)
                throw new PipeException(PipeErrorCodes.InvalidParams, "hotspot et falloff ne concernent qu'un projecteur (type spot).");

            var hotspot = reader.Has("hotspot") ? reader.RequireDouble("hotspot") : Format.Degrees(light.HotspotAngle);
            var falloff = reader.Has("falloff") ? reader.RequireDouble("falloff") : Format.Degrees(light.FalloffAngle);
            if (hotspot is <= 0 or >= 160 || falloff is <= 0 or > 160 || falloff < hotspot)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    "Le faisceau vif (hotspot) et le faisceau total (falloff) sont en degrés, entre 0 et 160, et falloff doit être au moins égal à hotspot.");

            light.SetHotspotAndFalloff(Format.Radians(hotspot), Format.Radians(falloff));
        }

        if (reader.Has("attenuation"))
            light.AttenuationType = ParseAttenuation(reader.RequireString("attenuation"));

        if (reader.Has("webFile") || creating && light.LightType == DrawableType.WebLight)
        {
            if (light.LightType != DrawableType.WebLight)
                throw new PipeException(PipeErrorCodes.InvalidParams, "webFile ne concerne qu'une lumière de type web.");

            var path = reader.RequireString("webFile").Trim();
            if (!File.Exists(path))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Fichier photométrique introuvable : {path}");
            light.WebFile = path;
        }
    }

    private static object DescribeLight(Light light) => new
    {
        Handle = light.Handle.ToString(),
        light.Name,
        Type = LightTypeName(light.LightType),
        light.IsOn,
        Intensity = Format.Number(IsPhotometric(light.Database) ? light.PhysicalIntensity : light.Intensity),
        IntensityFactor = IsPhotometric(light.Database) && Math.Abs(light.Intensity - 1) > 1e-9 ? Format.Number(light.Intensity) : (double?)null,
        Color = Format.Color(light.LightColor),
        Position = Format.Point(light.Position),
        Target = light.HasTarget ? Format.Point(light.TargetLocation) : null,
        Direction = Format.Vector(light.Direction),
        Shadows = light.Shadow.ShadowsOn,
        Hotspot = light.LightType == DrawableType.SpotLight ? Format.Degrees(light.HotspotAngle) : (double?)null,
        Falloff = light.LightType == DrawableType.SpotLight ? Format.Degrees(light.FalloffAngle) : (double?)null,
        Attenuation = light.AttenuationType.ToString(),
        WebFile = string.IsNullOrWhiteSpace(light.WebFile) ? null : light.WebFile,
        light.Layer,
        Space = light.OwnerId == SymbolUtilityServices.GetBlockModelSpaceId(light.Database) ? "model" : "paper",
    };

    private readonly record struct PlaceResult(bool Calibrated, string? Warning, CoordinateSystemTools.SunAnchor? Anchor);

    /// <summary>Clé du dictionnaire des objets nommés qui marque un lieu donné à la main (latitude et longitude).</summary>
    private const string ManualPlaceKey = "MCP_SUN_MANUAL_PLACE";

    /// <summary>
    /// Lieu, nord et fuseau du soleil. Un lieu explicite (latitude et longitude) est retenu et n'est plus recalé
    /// ensuite, sauf calibrate vrai. Sans lieu explicite, le lieu est calé sur le système de coordonnées quand
    /// calibrate le demande (par défaut, tant qu'aucun lieu manuel n'est fixé). north, utcOffset et daylightSaving
    /// s'appliquent même quand le lieu n'est pas trouvé. Le marqueur géographique du dessin n'est jamais modifié.
    /// </summary>
    private static PlaceResult ApplyPlace(ToolContext context, ArgReader reader, Sun sun, bool dateChanged)
    {
        var database = context.Database;
        var explicitPlace = reader.Has("latitude") || reader.Has("longitude");
        if (explicitPlace && !(reader.Has("latitude") && reader.Has("longitude")))
            throw new PipeException(PipeErrorCodes.InvalidParams, "latitude et longitude vont ensemble, en degrés.");

        Point3d? at = reader.Has("at") ? reader.RequirePoint("at") : null;
        var calibrate = reader.Has("calibrate")
            ? reader.GetBool("calibrate", true)
            : at is not null || !IsManualPlace(context);

        // Ancre dans le système du dessin (at, sinon centre de l'emprise) : nord de la projection et fuseau.
        CoordinateSystemTools.SunAnchor? anchor = null;
        string? locateError = null;
        if (explicitPlace || calibrate)
        {
            if (CoordinateSystemTools.TryLocate(database, at?.X, at?.Y, out var found, out var error))
                anchor = found;
            else
                locateError = error;
        }

        string? warning = null;
        double? latitude = null;
        double? longitude = null;
        if (explicitPlace)
        {
            latitude = reader.RequireDouble("latitude");
            longitude = reader.RequireDouble("longitude");
            if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
                throw new PipeException(PipeErrorCodes.InvalidParams, "Latitude de -90 à 90, longitude de -180 à 180, en degrés.");
        }
        else if (calibrate)
        {
            if (anchor is { } located)
            {
                latitude = located.Latitude;
                longitude = located.Longitude;
            }
            else
            {
                warning = locateError + " Le soleil garde son lieu actuel. Indiquez latitude et longitude, ou attribuez un système avec set_coordinate_system.";
            }
        }

        double? north = null;
        if (reader.Has("north"))
        {
            north = reader.RequireDouble("north");
            if (north is < -180 or > 180)
                throw new PipeException(PipeErrorCodes.InvalidParams, "Le nord se donne en degrés, de -180 à 180, dans le sens horaire depuis +Y.");
        }
        else if (latitude is not null && anchor is { } origin)
        {
            north = GridNorthDegrees(database, origin);
        }

        double? utcOffset = reader.Has("utcOffset") ? reader.RequireDouble("utcOffset") : null;
        bool? daylightSaving = reader.Has("daylightSaving") ? reader.GetBool("daylightSaving", false) : null;
        var local = sun.DateTime == default ? DateTime.Now : sun.DateTime;

        if (latitude is double lat && longitude is double lon)
        {
            database.Latitude = lat;
            database.Longitude = lon;
            SetManualPlace(context, explicitPlace);
        }

        if (north is double northDegrees)
            database.NorthDirection = Format.Radians(northDegrees);

        if (latitude is not null || utcOffset is not null)
        {
            SunCalibration.TimeChoice time;
            try
            {
                time = SunCalibration.InferTime(
                    anchor?.Code,
                    longitude ?? database.Longitude,
                    latitude ?? database.Latitude,
                    local,
                    utcOffset,
                    daylightSaving);
            }
            catch (ArgumentException ex)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams, ex.Message);
            }

            if (time.Zone == "Paris")
                database.TimeZone = Autodesk.AutoCAD.DatabaseServices.TimeZone.Paris;
            else
                database.SetTimeZoneAsUtcOffset(time.UtcOffsetHours);

            sun.IsDaylightSavingsOn = time.DaylightSaving;
            if (time.Source.StartsWith("solaire", StringComparison.Ordinal))
                warning = Join(warning, "Fuseau solaire, pas l'heure légale : indiquez utcOffset pour forcer le décalage civil.");
        }
        else if (daylightSaving is bool forced)
        {
            sun.IsDaylightSavingsOn = forced;
        }
        else if (dateChanged && database.TimeZone == Autodesk.AutoCAD.DatabaseServices.TimeZone.Paris)
        {
            sun.IsDaylightSavingsOn = SunCalibration.IsEuropeanDaylightSaving(local);
        }

        return new PlaceResult(latitude is not null && !explicitPlace, warning, anchor);
    }

    private static double? GridNorthDegrees(Database database, CoordinateSystemTools.SunAnchor origin)
    {
        foreach (var distance in new[] { 1000d, 100d, 10d, 1d })
        {
            if (CoordinateSystemTools.TryLocate(database, origin.X, origin.Y + distance, out var north, out _))
                return SunCalibration.NorthDirectionDegrees(origin.Longitude, origin.Latitude, north.Longitude, north.Latitude);
        }

        return null;
    }

    private static bool IsManualPlace(ToolContext context)
    {
        var named = (DBDictionary)context.RequireTransaction().GetObject(context.Database.NamedObjectsDictionaryId, OpenMode.ForRead);
        return named.Contains(ManualPlaceKey);
    }

    private static void SetManualPlace(ToolContext context, bool manual)
    {
        var transaction = context.RequireTransaction();
        var named = (DBDictionary)transaction.GetObject(context.Database.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (named.Contains(ManualPlaceKey) == manual)
            return;

        named.UpgradeOpen();
        if (manual)
        {
            var record = new Xrecord
            {
                Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, "Latitude et longitude du soleil fixées à la main par set_sun")),
            };
            named.SetAt(ManualPlaceKey, record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }
        else
        {
            var id = named.GetAt(ManualPlaceKey);
            named.Remove(ManualPlaceKey);
            transaction.GetObject(id, OpenMode.ForWrite).Erase();
        }
    }

    private static object DescribeSun(
        ToolContext context,
        DBObject view,
        string kind,
        bool? calibratedNow,
        string? warning,
        CoordinateSystemTools.SunAnchor? usedAnchor = null)
    {
        var database = context.Database;
        var sunId = SunId(view);
        Sun? sun = !sunId.IsNull && !sunId.IsErased
            ? (Sun)context.RequireTransaction().GetObject(sunId, OpenMode.ForRead)
            : null;
        // Ancre décrite : le point réellement utilisé par set_sun (at compris), sinon le centre de l'emprise.
        string? locateError = null;
        var anchor = usedAnchor ?? (CoordinateSystemTools.TryLocate(database, null, null, out var located, out locateError) ? located : default);
        // LATITUDE et LONGITUDE sont stockées en degrés décimaux, contrairement aux angles du dessin.
        var latitude = Format.Number(database.Latitude);
        var longitude = Format.Number(database.Longitude);
        var manual = IsManualPlace(context);
        var calibrated = calibratedNow ?? !manual && anchor.Code is not null
            && Math.Abs(latitude - anchor.Latitude) < 0.02
            && Math.Abs(NormalizeLongitude(longitude - anchor.Longitude)) < 0.02;
        var assumed = anchor.Source == "lambert93";

        return new
        {
            View = kind,
            Handle = view.Handle.ToString(),
            On = sun?.IsOn ?? false,
            Date = sun is null || sun.DateTime == default ? null : sun.DateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Time = sun is null || sun.DateTime == default ? null : sun.DateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            DaylightSaving = sun?.IsDaylightSavingsOn,
            Shadows = sun?.ShadowParameters.ShadowsOn,
            Intensity = sun is null ? (double?)null : Format.Number(sun.Intensity),
            Color = sun is null ? null : Format.Color(sun.SunColor),
            // Azimut et hauteur du soleil sont déjà en degrés dans l'API, contrairement aux angles du dessin.
            Azimuth = sun is null ? (double?)null : Format.Number(sun.Azimuth),
            Altitude = sun is null ? (double?)null : Format.Number(sun.Altitude),
            Direction = sun is null ? null : Format.Vector(sun.SunDirection),
            Latitude = latitude,
            Longitude = longitude,
            NorthDirection = Format.Degrees(database.NorthDirection),
            TimeZone = database.TimeZone.ToString(),
            UtcOffset = (double)database.TimeZoneOffset(database.TimeZone),
            CoordinateSystem = assumed ? null : anchor.Code,
            AssumedCoordinateSystem = assumed ? anchor.Code + " (supposé d'après l'emprise, aucun système attribué)" : null,
            ManualPlace = manual ? true : (bool?)null,
            Calibrated = calibrated,
            Anchor = anchor.Code is null ? null : new
            {
                anchor.X,
                anchor.Y,
                Longitude = Format.Number(anchor.Longitude),
                Latitude = Format.Number(anchor.Latitude),
                anchor.Source,
            },
            Warning = Join(warning, calibrated || manual || anchor.Code is not null ? null : locateError),
        };
    }

    private static void ApplyBackground(ToolContext context, DBObject view, ArgReader reader)
    {
        var type = reader.RequireString("background").Trim().ToLowerInvariant();
        var transaction = context.RequireTransaction();
        var database = context.Database;
        Background? background = type switch
        {
            "none" or "aucun" => null,
            "solid" or "uni" => new SolidBackground { Color = ToEntityColor(EditTools.ParseColor(reader.GetString("color") ?? "7")) },
            "gradient" or "degrade" or "dégradé" => Gradient(reader),
            "image" => Image(reader),
            "sky" or "ciel" => new SkyBackground { SunId = EnsureSun(transaction, view).ObjectId },
            _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Arrière-plan « {type} » inconnu : none, solid, gradient, image ou sky."),
        };

        // Un arrière-plan créé par l'outil par vue, nommé d'après le handle de la vue : le précédent est effacé
        // au lieu de s'accumuler. L'effacement le retire du dictionnaire (et U le rétablit) : pas de Remove, qui
        // laisserait l'objet sans entrée au moment de son effacement.
        var baseKey = "MCP_" + view.Handle;
        var stage = "ouverture du dictionnaire des arrière-plans";
        try
        {
            var dictionary = (DBDictionary)transaction.GetObject(Background.GetBackgroundDictionaryId(database, true), OpenMode.ForWrite);
            stage = "effacement de l'arrière-plan précédent";
            var previous = new List<ObjectId>();
            foreach (DBDictionaryEntry entry in dictionary)
            {
                if (entry.Key.StartsWith(baseKey, StringComparison.OrdinalIgnoreCase)
                    && (entry.Key.Length == baseKey.Length || entry.Key[baseKey.Length] == '_'))
                    previous.Add(entry.Value);
            }

            foreach (var id in previous)
            {
                if (BackgroundId(view) == id)
                    SetBackground(view, ObjectId.Null);
                // AutoCAD peut avoir déjà effacé l'arrière-plan quand la vue l'a lâché.
                if (!id.IsErased)
                    transaction.GetObject(id, OpenMode.ForWrite).Erase();
            }

            if (background is null)
            {
                SetBackground(view, ObjectId.Null);
                return;
            }

            stage = "ajout du nouvel arrière-plan";
            var key = baseKey;
            for (var i = 2; dictionary.Contains(key); i++)
                key = $"{baseKey}_{i}";
            dictionary.SetAt(key, background);
            transaction.AddNewlyCreatedDBObject(background, true);
            stage = "affectation à la vue";
            SetBackground(view, background.ObjectId);
        }
        catch (AcException ex)
        {
            Log.Error($"Arrière-plan, {stage}", ex);
            throw new PipeException(PipeErrorCodes.Internal, $"Arrière-plan, {stage} : {ex.ErrorStatus}");
        }
    }

    private static GradientBackground Gradient(ArgReader reader)
    {
        var gradient = new GradientBackground
        {
            ColorTop = ToEntityColor(EditTools.ParseColor(reader.GetString("colorTop") ?? reader.GetString("color") ?? "5")),
            ColorMiddle = ToEntityColor(EditTools.ParseColor(reader.GetString("colorMiddle") ?? "255,255,255")),
            ColorBottom = ToEntityColor(EditTools.ParseColor(reader.GetString("colorBottom") ?? "80,120,60")),
            Horizon = UnitInterval(reader, "horizon", 0.5),
            Height = UnitInterval(reader, "height", 0.5),
            Rotation = Format.Radians(reader.GetDouble("rotation", 0)),
        };
        return gradient;
    }

    private static ImageBackground Image(ArgReader reader)
    {
        var path = reader.RequireString("image").Trim();
        if (!File.Exists(path))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Image d'arrière-plan introuvable : {path}");

        return new ImageBackground { ImageFileName = path, FitToScreen = true };
    }

    private static object DescribeAmbiance(ToolContext context, DBObject view, string kind)
    {
        var transaction = context.RequireTransaction();
        var backgroundId = BackgroundId(view);
        object? background = new { Type = "none" };
        if (!backgroundId.IsNull && !backgroundId.IsErased)
        {
            background = transaction.GetObject(backgroundId, OpenMode.ForRead) switch
            {
                SolidBackground solid => new { Type = "solid", Color = DescribeEntityColor(solid.Color) },
                GradientBackground gradient => new
                {
                    Type = "gradient",
                    ColorTop = DescribeEntityColor(gradient.ColorTop),
                    ColorMiddle = DescribeEntityColor(gradient.ColorMiddle),
                    ColorBottom = DescribeEntityColor(gradient.ColorBottom),
                    Horizon = Format.Number(gradient.Horizon),
                    Height = Format.Number(gradient.Height),
                    Rotation = Format.Degrees(gradient.Rotation),
                },
                ImageBackground image => new { Type = "image", Image = image.ImageFileName },
                SkyBackground => new { Type = "sky" },
                DBObject other => new { Type = other.GetType().Name },
            };
        }

        var (lightingOn, lightingType) = DefaultLighting(view);
        var visualStyle = ModelingTools.VisualStyleName(context.Database, transaction, VisualStyleId(view));
        var hidden = !backgroundId.IsNull && !backgroundId.IsErased
            && string.Equals(visualStyle, "2dWireframe", StringComparison.OrdinalIgnoreCase);
        return new
        {
            View = kind,
            Handle = view.Handle.ToString(),
            VisualStyle = visualStyle,
            Brightness = Format.Number(Brightness(view)),
            Contrast = Format.Number(Contrast(view)),
            DefaultLighting = !lightingOn ? "off" : lightingType == DefaultLightingType.OneDistantLight ? "one" : "two",
            Background = background,
            Warning = hidden
                ? "Le style visuel Filaire 2D n'affiche pas l'arrière-plan : passez la vue en realistic, shaded ou conceptual (visualStyle)."
                : null,
        };
    }

    private static ObjectId VisualStyleId(DBObject view) => view switch
    {
        ViewportTableRecord record => record.VisualStyleId,
        Viewport viewport => viewport.VisualStyleId,
        _ => ObjectId.Null,
    };

    private static void SetVisualStyle(DBObject view, ObjectId id)
    {
        switch (view)
        {
            case ViewportTableRecord record: record.VisualStyleId = id; break;
            case Viewport viewport: viewport.VisualStyleId = id; break;
        }
    }

    private static (DBObject View, string Kind) OpenView(ToolContext context, ArgReader reader, OpenMode mode)
    {
        var transaction = context.RequireTransaction();
        var database = context.Database;
        if (reader.Has("viewport"))
        {
            var id = Handles.Resolve(context, reader, "viewport")[0];
            var obj = transaction.GetObject(id, mode);
            return obj switch
            {
                Viewport => (obj, "paper"),
                ViewportTableRecord => (obj, "model"),
                _ => throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas une fenêtre de vue."),
            };
        }

        if (!database.TileMode)
        {
            try
            {
                var active = context.Editor.ActiveViewportId;
                if (!active.IsNull)
                {
                    var obj = transaction.GetObject(active, mode);
                    if (obj is Viewport or ViewportTableRecord)
                        return (obj, obj is Viewport ? "paper" : "model");
                }
            }
            catch (AcException)
            {
                // Vue active indisponible : on retombe sur la fenêtre de l'espace objet.
            }
        }

        // Vue de l'espace objet : la table des fenêtres est d'abord mise à jour depuis l'écran, pour que la
        // modification parte de la vue affichée ; RefreshAfterCommit la renvoie ensuite à l'écran.
        if (mode == OpenMode.ForWrite)
        {
            try
            {
                context.Editor.UpdateTiledViewportsInDatabase();
            }
            catch (AcException)
            {
                // Pas de vue affichée à synchroniser : la table des fenêtres fait foi.
            }
        }

        return ((ViewportTableRecord)transaction.GetObject(database.CurrentViewportTableRecordId, mode), "model");
    }

    /// <summary>
    /// Après validation : une vue de l'espace objet (enregistrement de la table des fenêtres) n'est affichée
    /// qu'une fois renvoyée à l'écran ; une fenêtre de présentation se régénère simplement.
    /// </summary>
    private static void RefreshAfterCommit(ToolContext context, DBObject view)
    {
        var tiled = view is ViewportTableRecord;
        context.AfterCommit(() =>
        {
            if (tiled)
                context.Editor.UpdateTiledViewportsFromDatabase();
            context.Editor.Regen();
        });
    }

    private static Sun EnsureSun(Transaction transaction, DBObject view)
    {
        var sunId = SunId(view);
        if (!sunId.IsNull && !sunId.IsErased)
            return (Sun)transaction.GetObject(sunId, OpenMode.ForWrite);

        var sun = new Sun { IsOn = true };
        switch (view)
        {
            case ViewportTableRecord record:
                record.SetSun(sun);
                break;
            case Viewport viewport:
                viewport.SetSun(sun);
                break;
            default:
                throw new PipeException(PipeErrorCodes.InvalidParams, "Cette vue n'a pas de soleil.");
        }

        // SetSun ajoute le soleil au dessin mais le laisse ouvert : la transaction doit le prendre en charge pour le
        // fermer à la validation, sinon il reste ouvert en écriture et bloque les appels suivants (eWasOpenForWrite).
        transaction.AddNewlyCreatedDBObject(sun, true);
        return sun;
    }

    private static ObjectId SunId(DBObject view) => view switch
    {
        ViewportTableRecord record => record.SunId,
        Viewport viewport => viewport.SunId,
        _ => ObjectId.Null,
    };

    private static ObjectId BackgroundId(DBObject view) => view switch
    {
        ViewportTableRecord record => record.Background,
        Viewport viewport => viewport.Background,
        _ => ObjectId.Null,
    };

    private static void SetBackground(DBObject view, ObjectId id)
    {
        switch (view)
        {
            case ViewportTableRecord record: record.Background = id; break;
            case Viewport viewport: viewport.Background = id; break;
        }
    }

    private static double Brightness(DBObject view) => view switch
    {
        ViewportTableRecord record => record.Brightness,
        Viewport viewport => viewport.Brightness,
        _ => 0,
    };

    private static void SetBrightness(DBObject view, double value)
    {
        try
        {
            switch (view)
            {
                case ViewportTableRecord record: record.Brightness = value; break;
                case Viewport viewport: viewport.Brightness = value; break;
            }
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Luminosité refusée : {ex.Message}");
        }
    }

    private static double Contrast(DBObject view) => view switch
    {
        ViewportTableRecord record => record.Contrast,
        Viewport viewport => viewport.Contrast,
        _ => 0,
    };

    private static void SetContrast(DBObject view, double value)
    {
        try
        {
            switch (view)
            {
                case ViewportTableRecord record: record.Contrast = value; break;
                case Viewport viewport: viewport.Contrast = value; break;
            }
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Contraste refusé : {ex.Message}");
        }
    }

    private static (bool On, DefaultLightingType Type) DefaultLighting(DBObject view) => view switch
    {
        ViewportTableRecord record => (record.DefaultLightingOn, record.DefaultLightingType),
        Viewport viewport => (viewport.DefaultLightingOn, viewport.DefaultLightingType),
        _ => (false, DefaultLightingType.TwoDistantLights),
    };

    private static void ApplyDefaultLighting(DBObject view, string text)
    {
        var (on, type) = text.Trim().ToLowerInvariant() switch
        {
            "off" or "aucun" or "eteint" or "éteint" => (false, DefaultLightingType.TwoDistantLights),
            "one" or "une" or "1" => (true, DefaultLightingType.OneDistantLight),
            "two" or "deux" or "2" => (true, DefaultLightingType.TwoDistantLights),
            _ => throw new PipeException(PipeErrorCodes.InvalidParams, "Éclairage par défaut : off, one ou two."),
        };
        switch (view)
        {
            case ViewportTableRecord record:
                record.DefaultLightingType = type;
                record.DefaultLightingOn = on;
                break;
            case Viewport viewport:
                viewport.DefaultLightingType = type;
                viewport.DefaultLightingOn = on;
                break;
        }
    }

    private static IEnumerable<Light> CollectLights(ToolContext context)
    {
        var transaction = context.RequireTransaction();
        var table = (BlockTable)transaction.GetObject(context.Database.BlockTableId, OpenMode.ForRead);
        foreach (var blockId in table)
        {
            var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (!block.IsLayout)
                continue;

            foreach (var id in block)
            {
                if (transaction.GetObject(id, OpenMode.ForRead) is Light light)
                    yield return light;
            }
        }
    }

    private static void AppendLight(ToolContext context, Light light, string? layer)
    {
        if (layer is not null)
            light.Layer = RequireLayer(context, layer);

        var space = (BlockTableRecord)context.RequireTransaction().GetObject(context.Database.CurrentSpaceId, OpenMode.ForWrite);
        space.AppendEntity(light);
        context.RequireTransaction().AddNewlyCreatedDBObject(light, true);
    }

    private static string RequireLayer(ToolContext context, string name)
    {
        var table = (LayerTable)context.RequireTransaction().GetObject(context.Database.LayerTableId, OpenMode.ForRead);
        if (!table.Has(name))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le calque « {name} » n'existe pas. Créez-le d'abord avec create_layer.");
        return name;
    }

    private static string NextLightName(ToolContext context)
    {
        var names = CollectLights(context).Select(light => light.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
        {
            var name = $"Lumière {i}";
            if (!names.Contains(name))
                return name;
        }
    }

    private static ObjectId RequireMaterial(Transaction transaction, DBDictionary dictionary, string name)
    {
        var id = FindEntry(dictionary, name);
        return id is not null && transaction.GetObject(id.Value, OpenMode.ForRead) is Material
            ? id.Value
            : throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Matériau « {name} » introuvable. Créez-le avec set_material, ou listez-les avec list_materials.");
    }

    private static ObjectId? FindEntry(DBDictionary dictionary, string name)
    {
        foreach (DBDictionaryEntry entry in dictionary)
        {
            if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                return entry.Value;
        }

        return null;
    }

    private static void EnsureWritable(Transaction transaction, Entity entity)
    {
        var layer = (LayerTableRecord)transaction.GetObject(entity.LayerId, OpenMode.ForRead);
        if (layer.IsLocked)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"L'objet {entity.Handle} est sur le calque verrouillé « {entity.Layer} » : déverrouillez-le d'abord.");
    }

    private static DrawableType ParseLightType(string text) => text.Trim().ToLowerInvariant() switch
    {
        "point" or "ponctuelle" => DrawableType.PointLight,
        "spot" or "projecteur" => DrawableType.SpotLight,
        "distant" or "lointaine" or "directionnelle" => DrawableType.DistantLight,
        "web" or "photometrique" or "photométrique" => DrawableType.WebLight,
        _ => throw new PipeException(PipeErrorCodes.InvalidParams, "Type de lumière : point, spot, distant ou web."),
    };

    private static string LightTypeName(DrawableType type) => type switch
    {
        DrawableType.PointLight => "point",
        DrawableType.SpotLight => "spot",
        DrawableType.DistantLight => "distant",
        DrawableType.WebLight => "web",
        _ => type.ToString(),
    };

    private static AttenuationType ParseAttenuation(string text) => text.Trim().ToLowerInvariant() switch
    {
        "none" or "aucune" => AttenuationType.None,
        "inverselinear" or "lineaire" or "linéaire" => AttenuationType.InverseLinear,
        "inversesquare" or "carre" or "carré" => AttenuationType.InverseSquare,
        _ => throw new PipeException(PipeErrorCodes.InvalidParams, "Atténuation : none, inverseLinear ou inverseSquare."),
    };

    private static bool IsPhotometric(Database database) =>
        (LightingUnits)database.LightingUnits != LightingUnits.LightingUnitsGeneric;

    private static string LightingUnitsName(Database database) => ((LightingUnits)database.LightingUnits) switch
    {
        LightingUnits.LightingUnitsAmerican => "american",
        LightingUnits.LightingUnitsInternational => "si",
        _ => "generic",
    };

    private static bool IsSpecial(string name) =>
        string.Equals(name, ByLayer, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "DuCalque", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, ByBlock, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "DuBloc", StringComparison.OrdinalIgnoreCase);

    private static string CanonicalSpecial(string name) =>
        string.Equals(name, ByBlock, StringComparison.OrdinalIgnoreCase) || string.Equals(name, "DuBloc", StringComparison.OrdinalIgnoreCase)
            ? ByBlock
            : ByLayer;

    private static string RequireMaterialName(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.IndexOfAny(['<', '>', '/', '\\', '"', ':', ';', '?', '*', '|']) >= 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » n'est pas un nom de matériau valide.");
        return name;
    }

    private static string RequireLightName(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le nom de la lumière est vide.");
        return name;
    }

    private static double UnitInterval(ArgReader reader, string name, double fallback)
    {
        if (!reader.Has(name))
            return fallback;

        var value = reader.RequireDouble(name);
        if (value is > 1 and <= 100)
            value /= 100;
        return value is >= 0 and <= 1
            ? value
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » doit être compris entre 0 et 1.");
    }

    private static EntityColor ToEntityColor(Color color)
    {
        if (color.ColorMethod == ColorMethod.ByColor)
            return new EntityColor(color.Red, color.Green, color.Blue);

        var displayed = color.ColorValue;
        return new EntityColor(displayed.R, displayed.G, displayed.B);
    }

    private static string DescribeEntityColor(EntityColor color) =>
        color.ColorMethod == ColorMethod.ByColor ? $"RGB({color.Red},{color.Green},{color.Blue})" : color.ColorIndex.ToString();

    private static double NormalizeLongitude(double delta)
    {
        var value = delta % 360;
        if (value > 180)
            value -= 360;
        if (value < -180)
            value += 360;
        return value;
    }

    private static string? Join(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) ? second : string.IsNullOrWhiteSpace(second) ? first : first + " " + second;
}
