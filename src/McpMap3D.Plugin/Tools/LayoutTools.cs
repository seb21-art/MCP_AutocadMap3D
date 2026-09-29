using System.Collections;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Outils de gestion des présentations (Layouts) et des fenêtres de présentation (Viewports).
/// </summary>
internal static class LayoutTools
{
    public static object ListLayouts(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var filter = NameFilter.Create(reader.GetStrings("names"));
        var transaction = context.RequireTransaction();
        var database = context.Database;

        var layoutsDict = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        var currentLayoutName = LayoutManager.Current.CurrentLayout;

        var list = new List<object>();
        foreach (DictionaryEntry entry in layoutsDict)
        {
            var name = (string)entry.Key;
            if (filter is not null && !filter.IsMatch(name))
                continue;

            var layout = (Layout)transaction.GetObject((ObjectId)entry.Value!, OpenMode.ForRead);
            var btr = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);

            var viewports = layout.GetViewports();
            var floatingViewportCount = layout.ModelType ? 0 : Math.Max(0, viewports.Count - 1);

            var paperSize = layout.PlotPaperSize;
            var isMillimeters = layout.PlotPaperUnits == PlotPaperUnit.Millimeters;

            list.Add(new
            {
                Name = layout.LayoutName,
                TabOrder = layout.TabOrder,
                IsCurrent = string.Equals(layout.LayoutName, currentLayoutName, StringComparison.OrdinalIgnoreCase),
                IsModel = layout.ModelType,
                CanonicalMediaName = string.IsNullOrEmpty(layout.CanonicalMediaName) ? null : layout.CanonicalMediaName,
                PlotConfiguration = string.IsNullOrEmpty(layout.PlotConfigurationName) ? null : layout.PlotConfigurationName,
                PaperWidth = Format.Number(paperSize.X),
                PaperHeight = Format.Number(paperSize.Y),
                PaperUnits = isMillimeters ? "mm" : "inches",
                ViewportCount = floatingViewportCount,
                TotalViewportCount = layout.ModelType ? 0 : viewports.Count,
                EntityCount = CountEntities(btr),
            });
        }

        list.Sort((a, b) => ((dynamic)a).TabOrder.CompareTo(((dynamic)b).TabOrder));

        return new
        {
            Count = list.Count,
            CurrentLayout = currentLayoutName,
            CurrentSpace = database.TileMode ? "model" : "paper",
            Layouts = list,
        };
    }

    public static object SetCurrentLayout(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var name = reader.RequireString("name").Trim();
        var database = context.Database;
        var lm = LayoutManager.Current;

        string targetName;
        bool isModel;

        if (string.Equals(name, "model", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "objet", StringComparison.OrdinalIgnoreCase))
        {
            targetName = "Model";
            isModel = true;
        }
        else
        {
            using var tr = database.TransactionManager.StartTransaction();
            var layouts = (DBDictionary)tr.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            var match = layouts.Cast<DictionaryEntry>().FirstOrDefault(e => string.Equals((string)e.Key, name, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null)
            {
                var available = layouts.Cast<DictionaryEntry>().Select(e => (string)e.Key).OrderBy(n => n).ToArray();
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Présentation « {name} » introuvable. Présentations disponibles : {string.Join(", ", available)}.");
            }

            targetName = (string)match.Key;
            var layout = (Layout)tr.GetObject((ObjectId)match.Value!, OpenMode.ForRead);
            isModel = layout.ModelType;
            tr.Commit();
        }

        var previousLayout = lm.CurrentLayout;
        if (!string.Equals(previousLayout, targetName, StringComparison.OrdinalIgnoreCase))
        {
            lm.CurrentLayout = targetName;
        }

        return new
        {
            Layout = targetName,
            Space = isModel ? "model" : "paper",
            PreviousLayout = previousLayout,
        };
    }

    public static object CreateLayout(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var name = reader.RequireString("name").Trim();
        var database = context.Database;

        if (string.Equals(name, "model", StringComparison.OrdinalIgnoreCase))
            throw new PipeException(PipeErrorCodes.InvalidParams, "« Model » est réservé à l'espace Objet.");

        try
        {
            SymbolUtilityServices.ValidateSymbolName(name, allowVerticalBar: false);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"« {name} » n'est pas un nom de présentation valide (caractères interdits : < > / \\ \" : ; ? * | , = `).");
        }

        using (var tr = database.TransactionManager.StartTransaction())
        {
            var layouts = (DBDictionary)tr.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            if (layouts.Contains(name))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"La présentation « {name} » existe déjà.");
            tr.Commit();
        }

        ObjectId layoutId;
        var copyFrom = reader.GetString("copyFrom");
        var lm = LayoutManager.Current;

        if (!string.IsNullOrWhiteSpace(copyFrom))
        {
            string exactCopyFrom;
            int newTabOrder;
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var layouts = (DBDictionary)tr.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
                var match = layouts.Cast<DictionaryEntry>().FirstOrDefault(e => string.Equals((string)e.Key, copyFrom, StringComparison.OrdinalIgnoreCase));
                if (match.Key is null)
                    throw new PipeException(PipeErrorCodes.InvalidParams, $"La présentation à copier « {copyFrom} » n'existe pas.");

                exactCopyFrom = (string)match.Key;
                newTabOrder = layouts.Count;
                tr.Commit();
            }

            lm.CloneLayout(exactCopyFrom, name, newTabOrder);
            layoutId = lm.GetLayoutId(name);
        }
        else
        {
            layoutId = lm.CreateLayout(name);
        }

        // Si la présentation est créée de zéro, ou si un format est explicitement demandé,
        // configurer sa mise en page dans le dessin (traceur PDF par défaut, format A3 paysage par défaut, 1:1, etc.)
        if (string.IsNullOrWhiteSpace(copyFrom) || reader.Has("paperSize"))
        {
            ConfigureLayoutPageSetup(database, layoutId, reader);
        }

        var setCurrent = reader.GetBool("current", false);
        if (setCurrent)
        {
            lm.CurrentLayout = name;
        }

        int tabOrder;
        Point2d paperSize;
        string? canonicalMedia;
        using (var tr = database.TransactionManager.StartTransaction())
        {
            var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
            tabOrder = layout.TabOrder;
            paperSize = layout.PlotPaperSize;
            canonicalMedia = layout.CanonicalMediaName;
            tr.Commit();
        }

        return new
        {
            Name = name,
            TabOrder = tabOrder,
            IsCurrent = setCurrent,
            CopiedFrom = copyFrom,
            PaperWidth = Format.Number(paperSize.X),
            PaperHeight = Format.Number(paperSize.Y),
            CanonicalMediaName = string.IsNullOrEmpty(canonicalMedia) ? null : canonicalMedia,
        };
    }

    private static void ConfigureLayoutPageSetup(Database database, ObjectId layoutId, ArgReader reader)
    {
        var paperSizeArg = reader.GetString("paperSize", "A3")!;
        var orientationArg = reader.GetString("orientation", "landscape")!;

        try
        {
            using var tr = database.TransactionManager.StartTransaction();
            var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
            var psv = PlotSettingsValidator.Current;

            // 1. Définir le périphérique PDF
            var devices = psv.GetPlotDeviceList();
            string pdfDev = "AutoCAD PDF (General Documentation).pc3";
            bool found = false;
            foreach (var d in devices)
            {
                if (d is string s && string.Equals(s, pdfDev, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                foreach (var d in devices)
                {
                    if (d is string s && string.Equals(s, "DWG To PDF.pc3", StringComparison.OrdinalIgnoreCase))
                    {
                        pdfDev = s;
                        found = true;
                        break;
                    }
                }
            }
            if (!found)
                pdfDev = "DWG To PDF.pc3";

            psv.SetPlotConfigurationName(layout, pdfDev, null);
            psv.RefreshLists(layout);

            // 2. Définir le format canonique (A3 paysage 420x297 mm par défaut)
            var mediaList = psv.GetCanonicalMediaNameList(layout);
            var matchedMedia = PlotTools.FindMediaName(psv, layout, mediaList, paperSizeArg, orientationArg, isLayout: true);

            if (matchedMedia is not null)
            {
                psv.SetCanonicalMediaName(layout, matchedMedia);
            }
            else
            {
                foreach (var m in mediaList)
                {
                    if (m is string s && s.Contains("A3", StringComparison.OrdinalIgnoreCase) && s.Contains("420", StringComparison.OrdinalIgnoreCase))
                    {
                        psv.SetCanonicalMediaName(layout, s);
                        break;
                    }
                }
            }

            psv.SetPlotPaperUnits(layout, PlotPaperUnit.Millimeters);
            psv.SetPlotType(layout, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
            psv.SetUseStandardScale(layout, true);
            psv.SetStdScaleType(layout, StdScaleType.StdScale1To1);
            psv.SetPlotRotation(layout, PlotRotation.Degrees000);

            tr.Commit();
        }
        catch (Exception ex)
        {
            Log.Info($"Configuration de la mise en page pour la présentation : {ex.Message}");
        }
    }

    public static object RenameLayout(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var oldName = reader.RequireString("oldName").Trim();
        var newName = reader.RequireString("newName").Trim();
        var database = context.Database;

        if (string.Equals(oldName, "model", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(newName, "model", StringComparison.OrdinalIgnoreCase))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Impossible de renommer l'espace Objet (« Model »).");

        try
        {
            SymbolUtilityServices.ValidateSymbolName(newName, allowVerticalBar: false);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"« {newName} » n'est pas un nom de présentation valide (caractères interdits : < > / \\ \" : ; ? * | , = `).");
        }

        string exactOldName;
        using (var tr = database.TransactionManager.StartTransaction())
        {
            var layouts = (DBDictionary)tr.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            var match = layouts.Cast<DictionaryEntry>().FirstOrDefault(e => string.Equals((string)e.Key, oldName, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"La présentation « {oldName} » n'existe pas.");

            exactOldName = (string)match.Key;
            if (layouts.Contains(newName))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Une présentation nommée « {newName} » existe déjà.");

            tr.Commit();
        }

        LayoutManager.Current.RenameLayout(exactOldName, newName);

        return new
        {
            OldName = exactOldName,
            NewName = newName,
            CurrentLayout = LayoutManager.Current.CurrentLayout,
        };
    }

    public static object DeleteLayout(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var name = reader.RequireString("name").Trim();
        var database = context.Database;
        var lm = LayoutManager.Current;

        string exactName;
        bool wasCurrent;
        using (var tr = database.TransactionManager.StartTransaction())
        {
            var layouts = (DBDictionary)tr.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            var match = layouts.Cast<DictionaryEntry>().FirstOrDefault(e => string.Equals((string)e.Key, name, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"La présentation « {name} » n'existe pas.");

            exactName = (string)match.Key;
            var layout = (Layout)tr.GetObject((ObjectId)match.Value!, OpenMode.ForRead);
            if (layout.ModelType)
                throw new PipeException(PipeErrorCodes.InvalidParams, "Impossible de supprimer l'espace Objet (« Model »).");

            var paperLayoutsCount = layouts.Cast<DictionaryEntry>()
                .Select(e => (Layout)tr.GetObject((ObjectId)e.Value!, OpenMode.ForRead))
                .Count(l => !l.ModelType);

            if (paperLayoutsCount <= 1)
                throw new PipeException(PipeErrorCodes.InvalidParams, "Impossible de supprimer la seule présentation restante du dessin.");

            wasCurrent = string.Equals(lm.CurrentLayout, exactName, StringComparison.OrdinalIgnoreCase);
            tr.Commit();
        }

        if (wasCurrent)
        {
            lm.CurrentLayout = "Model";
        }

        lm.DeleteLayout(exactName);

        return new
        {
            Deleted = exactName,
            CurrentLayout = lm.CurrentLayout,
        };
    }

    public static object ListViewports(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var layoutName = reader.GetString("layout");
        var transaction = context.RequireTransaction();
        var database = context.Database;

        Layout layout;
        var layoutsDict = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        if (!string.IsNullOrWhiteSpace(layoutName))
        {
            var match = layoutsDict.Cast<DictionaryEntry>().FirstOrDefault(e => string.Equals((string)e.Key, layoutName, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Présentation « {layoutName} » introuvable.");

            layout = (Layout)transaction.GetObject((ObjectId)match.Value!, OpenMode.ForRead);
        }
        else
        {
            var currentLayoutName = LayoutManager.Current.CurrentLayout;
            layout = (Layout)transaction.GetObject((ObjectId)layoutsDict[currentLayoutName], OpenMode.ForRead);
        }

        if (layout.ModelType)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "L'espace Objet n'a pas de fenêtres de présentation. Spécifiez une présentation ou basculez sur une présentation avec set_current_layout.");

        var includeOverall = reader.GetBool("includeOverall", false);
        var viewports = layout.GetViewports();
        var list = new List<object>();

        foreach (ObjectId id in viewports)
        {
            var vp = (Viewport)transaction.GetObject(id, OpenMode.ForRead);
            var isOverall = vp.Number == 1;

            if (isOverall && !includeOverall)
                continue;

            list.Add(new
            {
                Handle = vp.Handle.ToString(),
                Number = vp.Number,
                IsOverall = isOverall,
                Center = Format.Point(vp.CenterPoint),
                Width = Format.Number(vp.Width),
                Height = Format.Number(vp.Height),
                On = vp.On,
                Locked = vp.Locked,
                Layer = vp.Layer,
                ViewCenter = new { X = Format.Number(vp.ViewCenter.X), Y = Format.Number(vp.ViewCenter.Y) },
                CustomScale = Format.Number(vp.CustomScale),
                Scale = FormatScale(database, vp.CustomScale),
                StandardScale = vp.StandardScale.ToString(),
            });
        }

        return new
        {
            Layout = layout.LayoutName,
            Count = list.Count,
            Viewports = list,
        };
    }

    public static object CreateViewport(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var database = context.Database;
        var transaction = context.RequireTransaction();

        if (database.TileMode)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Vous êtes actuellement dans l'espace Objet. Basculez d'abord sur une présentation avec set_current_layout.");

        var center = reader.RequirePoint("center");
        var width = reader.RequireDouble("width");
        var height = reader.RequireDouble("height");
        if (width <= 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "La largeur de la fenêtre doit être strictement positive.");
        if (height <= 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "La hauteur de la fenêtre doit être strictement positive.");

        var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
        var vp = new Viewport
        {
            CenterPoint = center,
            Width = width,
            Height = height,
        };

        if (reader.Has("layer"))
        {
            var layerName = reader.GetString("layer")!;
            var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (!layers.Has(layerName))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Le calque « {layerName} » n'existe pas. Créez-le d'abord avec create_layer.");
            vp.Layer = layerName;
        }

        space.AppendEntity(vp);
        transaction.AddNewlyCreatedDBObject(vp, true);

        // Centre cible dans l'espace objet
        Point2d targetCenter;
        if (reader.Has("viewCenter"))
        {
            var vc = reader.RequirePoint("viewCenter");
            targetCenter = new Point2d(vc.X, vc.Y);
        }
        else if (database.Extmin.X <= database.Extmax.X)
        {
            targetCenter = new Point2d((database.Extmin.X + database.Extmax.X) / 2.0, (database.Extmin.Y + database.Extmax.Y) / 2.0);
        }
        else
        {
            targetCenter = Point2d.Origin;
        }

        vp.ViewTarget = Point3d.Origin;
        vp.ViewCenter = targetCenter;

        // Échelle
        if (reader.Has("customScale"))
        {
            var cs = reader.RequireDouble("customScale");
            if (cs <= 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "L'échelle doit être strictement positive.");
            vp.CustomScale = cs;
        }
        else if (reader.TryGet("scale", out var scaleElement))
        {
            vp.CustomScale = ParseScale(database, scaleElement);
        }
        else if (database.Extmin.Y < database.Extmax.Y)
        {
            var modelHeight = database.Extmax.Y - database.Extmin.Y;
            if (modelHeight > 0)
                vp.ViewHeight = modelHeight * 1.1;
            else
                vp.CustomScale = 1.0;
        }
        else
        {
            vp.CustomScale = 1.0;
        }

        var on = reader.GetBool("on", true);
        if (on)
        {
            vp.On = true;
            vp.UpdateDisplay();
        }

        var locked = reader.GetBool("locked", false);
        if (locked)
            vp.Locked = true;

        return new
        {
            Handle = vp.Handle.ToString(),
            Layout = LayoutManager.Current.CurrentLayout,
            Center = Format.Point(vp.CenterPoint),
            Width = Format.Number(vp.Width),
            Height = Format.Number(vp.Height),
            ViewCenter = new { X = Format.Number(vp.ViewCenter.X), Y = Format.Number(vp.ViewCenter.Y) },
            CustomScale = Format.Number(vp.CustomScale),
            Scale = FormatScale(database, vp.CustomScale),
            StandardScale = vp.StandardScale.ToString(),
            Locked = vp.Locked,
            On = vp.On,
            Layer = vp.Layer,
        };
    }

    public static object SetViewport(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;

        var id = Handles.Resolve(context, reader, "handle").First();
        var entity = (Entity)transaction.GetObject(id, OpenMode.ForWrite);
        if (entity is not Viewport vp)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas une fenêtre de présentation (Viewport).");

        if (vp.Number == 1)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Impossible de modifier la fenêtre globale de l'espace papier (Number = 1).");

        if (reader.Has("center"))
            vp.CenterPoint = reader.RequirePoint("center");

        if (reader.Has("width"))
        {
            var w = reader.RequireDouble("width");
            if (w <= 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "La largeur de la fenêtre doit être strictement positive.");
            vp.Width = w;
        }

        if (reader.Has("height"))
        {
            var h = reader.RequireDouble("height");
            if (h <= 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "La hauteur de la fenêtre doit être strictement positive.");
            vp.Height = h;
        }

        if (reader.Has("viewCenter"))
        {
            var vc = reader.RequirePoint("viewCenter");
            vp.ViewCenter = new Point2d(vc.X, vc.Y);
        }

        if (reader.Has("customScale"))
        {
            var cs = reader.RequireDouble("customScale");
            if (cs <= 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "L'échelle doit être strictement positive.");
            vp.CustomScale = cs;
        }
        else if (reader.TryGet("scale", out var scaleElement))
        {
            vp.CustomScale = ParseScale(database, scaleElement);
        }

        if (reader.Has("locked"))
            vp.Locked = reader.GetBool("locked", false);

        if (reader.Has("on"))
            vp.On = reader.GetBool("on", true);

        if (vp.On)
            vp.UpdateDisplay();

        if (reader.Has("layer"))
        {
            var layerName = reader.GetString("layer")!;
            var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (!layers.Has(layerName))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Le calque « {layerName} » n'existe pas. Créez-le d'abord avec create_layer.");
            vp.Layer = layerName;
        }

        return new
        {
            Handle = vp.Handle.ToString(),
            Number = vp.Number,
            Center = Format.Point(vp.CenterPoint),
            Width = Format.Number(vp.Width),
            Height = Format.Number(vp.Height),
            ViewCenter = new { X = Format.Number(vp.ViewCenter.X), Y = Format.Number(vp.ViewCenter.Y) },
            CustomScale = Format.Number(vp.CustomScale),
            Scale = FormatScale(database, vp.CustomScale),
            StandardScale = vp.StandardScale.ToString(),
            Locked = vp.Locked,
            On = vp.On,
            Layer = vp.Layer,
        };
    }

    public static double ParseScale(Database database, JsonElement scaleElement)
    {
        if (scaleElement.ValueKind == JsonValueKind.Number)
        {
            var val = scaleElement.GetDouble();
            if (val <= 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "L'échelle doit être strictement positive.");
            return val;
        }

        if (scaleElement.ValueKind != JsonValueKind.String)
            throw new PipeException(PipeErrorCodes.InvalidParams, "L'échelle attend un nombre ou une chaîne de ratio (ex: 0.002, \"1:500\", \"1/1000\").");

        var text = scaleElement.GetString()!.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var direct) && direct > 0)
            return direct;

        var parts = text.Split([':', '/'], 2);
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
            num > 0 && den > 0)
        {
            var ratio = num / den;

            var drawingUnitInMm = database.Insunits switch
            {
                UnitsValue.Meters => 1000.0,
                UnitsValue.Centimeters => 10.0,
                UnitsValue.Millimeters => 1.0,
                UnitsValue.Kilometers => 1000000.0,
                UnitsValue.Inches => 25.4,
                UnitsValue.Feet => 304.8,
                _ => 1.0,
            };

            return ratio * drawingUnitInMm;
        }

        throw new PipeException(PipeErrorCodes.InvalidParams,
            $"Format d'échelle invalide « {text} ». Utilisez par exemple 0.002, \"1:500\" ou \"1/1000\".");
    }

    public static string FormatScale(Database database, double customScale)
    {
        if (customScale <= 0)
            return Format.Number(customScale).ToString(CultureInfo.InvariantCulture);

        var drawingUnitInMm = database.Insunits switch
        {
            UnitsValue.Meters => 1000.0,
            UnitsValue.Centimeters => 10.0,
            UnitsValue.Millimeters => 1.0,
            UnitsValue.Kilometers => 1000000.0,
            UnitsValue.Inches => 25.4,
            UnitsValue.Feet => 304.8,
            _ => 1.0,
        };

        var ratio = customScale / drawingUnitInMm;
        if (ratio > 0)
        {
            var denominator = 1.0 / ratio;
            if (Math.Abs(denominator - Math.Round(denominator)) < 0.01 && Math.Round(denominator) >= 1)
                return $"1:{Math.Round(denominator)}";

            var numerator = ratio;
            if (Math.Abs(numerator - Math.Round(numerator)) < 0.01 && Math.Round(numerator) >= 1)
                return $"{Math.Round(numerator)}:1";
        }

        return Format.Number(customScale).ToString(CultureInfo.InvariantCulture);
    }

    private static int CountEntities(BlockTableRecord btr)
    {
        var count = 0;
        foreach (var _ in btr)
            count++;
        return count;
    }
}
