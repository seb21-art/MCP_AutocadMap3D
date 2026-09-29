using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Outils d'impression et d'export PDF (Plotting).
/// </summary>
internal static class PlotTools
{
    private const string DefaultPdfDevice1 = "AutoCAD PDF (General Documentation).pc3";
    private const string DefaultPdfDevice2 = "DWG To PDF.pc3";

    /// <summary>
    /// Liste les périphériques d'impression disponibles (fichiers PC3 et imprimantes système).
    /// Si un périphérique est spécifié, liste également les formats de papier disponibles pour ce périphérique.
    /// </summary>
    public static object ListPlotDevices(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var targetDevice = reader.GetString("device");
        var psv = PlotSettingsValidator.Current;

        var deviceCollection = psv.GetPlotDeviceList();
        var devices = new List<string>(deviceCollection.Count);
        foreach (var dev in deviceCollection)
        {
            if (dev is string s)
                devices.Add(s);
        }

        devices.Sort(StringComparer.OrdinalIgnoreCase);

        // Si aucun périphérique spécifique n'est demandé, renvoie la liste globale
        if (string.IsNullOrWhiteSpace(targetDevice))
        {
            var pdfDevices = devices.Where(d => d.Contains("PDF", StringComparison.OrdinalIgnoreCase)).ToList();
            var printers = devices.Where(d => !d.EndsWith(".pc3", StringComparison.OrdinalIgnoreCase)).ToList();
            var pc3Files = devices.Where(d => d.EndsWith(".pc3", StringComparison.OrdinalIgnoreCase)).ToList();

            return new
            {
                Count = devices.Count,
                PdfDevices = pdfDevices,
                Pc3Devices = pc3Files,
                SystemPrinters = printers,
                AllDevices = devices,
            };
        }

        // Périphérique spécifique : trouver la correspondance exacte ou approchée
        var matchedDevice = FindDevice(devices, targetDevice)
            ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Périphérique d'impression « {targetDevice} » introuvable. Périphériques disponibles : {string.Join(", ", devices)}.");

        // Charger les formats de papier pour ce périphérique
        using var tempSettings = new PlotSettings(true);
        psv.SetPlotConfigurationName(tempSettings, matchedDevice, null);
        psv.RefreshLists(tempSettings);

        var mediaList = psv.GetCanonicalMediaNameList(tempSettings);
        var mediaDetails = new List<object>(mediaList.Count);
        foreach (var canonicalObj in mediaList)
        {
            if (canonicalObj is string canonical)
            {
                var localName = psv.GetLocaleMediaName(tempSettings, canonical);
                mediaDetails.Add(new
                {
                    CanonicalName = canonical,
                    LocalName = localName,
                });
            }
        }

        return new
        {
            Device = matchedDevice,
            IsPc3 = matchedDevice.EndsWith(".pc3", StringComparison.OrdinalIgnoreCase),
            IsPdf = matchedDevice.Contains("PDF", StringComparison.OrdinalIgnoreCase),
            MediaCount = mediaDetails.Count,
            MediaSizes = mediaDetails,
        };
    }

    /// <summary>
    /// Liste les tables de styles de tracé (fichiers CTB et STB) disponibles.
    /// </summary>
    public static object ListPlotStyles(ToolContext context, JsonElement? args)
    {
        var psv = PlotSettingsValidator.Current;
        var styleCollection = psv.GetPlotStyleSheetList();

        var styles = new List<string>(styleCollection.Count);
        foreach (var s in styleCollection)
        {
            if (s is string str)
                styles.Add(str);
        }

        styles.Sort(StringComparer.OrdinalIgnoreCase);

        var ctb = styles.Where(s => s.EndsWith(".ctb", StringComparison.OrdinalIgnoreCase)).ToList();
        var stb = styles.Where(s => s.EndsWith(".stb", StringComparison.OrdinalIgnoreCase)).ToList();

        return new
        {
            Count = styles.Count,
            ColorDependent = ctb,
            Named = stb,
            AllStyles = styles,
        };
    }

    /// <summary>
    /// Exporte une présentation ou l'espace objet en fichier PDF.
    /// </summary>
    public static object ExportPdf(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var doc = context.RequireDocument();

        // 1. Choix du périphérique PDF
        var psv = PlotSettingsValidator.Current;
        var deviceList = psv.GetPlotDeviceList();
        var preferredDevice = reader.GetString("pdfDevice");
        var pdfDevice = ResolvePdfDevice(deviceList, preferredDevice);

        // 2. Détermination du chemin de sortie
        var layoutNameArg = reader.GetString("layout");
        var targetPath = reader.GetString("filePath");

        var effectivePath = ResolveOutputPdfPath(doc, layoutNameArg, targetPath);

        // 3. Exécution du tracé vers le fichier PDF
        return ExecutePlot(doc, psv, pdfDevice, effectivePath, isPlotToFile: true, reader, isPdfExport: true);
    }

    /// <summary>
    /// Trace une présentation ou l'espace objet vers un traceur, imprimante ou fichier.
    /// </summary>
    public static object PlotDrawing(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var doc = context.RequireDocument();
        var psv = PlotSettingsValidator.Current;

        var deviceName = reader.RequireString("device").Trim();
        var deviceList = psv.GetPlotDeviceList();
        var devices = new List<string>(deviceList.Count);
        foreach (var d in deviceList)
        {
            if (d is string s)
                devices.Add(s);
        }

        var matchedDevice = FindDevice(devices, deviceName)
            ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Périphérique d'impression « {deviceName} » introuvable. Utilisez list_plot_devices pour consulter la liste.");

        var filePath = reader.GetString("filePath");
        var isFilePlotter = matchedDevice.EndsWith(".pc3", StringComparison.OrdinalIgnoreCase) ||
                           matchedDevice.Contains("PDF", StringComparison.OrdinalIgnoreCase) ||
                           matchedDevice.Contains("DWF", StringComparison.OrdinalIgnoreCase);

        bool plotToFile;
        string? effectivePath = null;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            plotToFile = true;
            effectivePath = Path.GetFullPath(filePath);
        }
        else if (isFilePlotter)
        {
            var layoutNameArg = reader.GetString("layout");
            var ext = matchedDevice.Contains("PDF", StringComparison.OrdinalIgnoreCase) ? ".pdf" : ".plt";
            effectivePath = ResolveOutputPathWithExtension(doc, layoutNameArg, ext);
            plotToFile = true;
        }
        else
        {
            plotToFile = false;
        }

        return ExecutePlot(doc, psv, matchedDevice, effectivePath, plotToFile, reader, isPdfExport: false);
    }

    private static object ExecutePlot(
        Document doc,
        PlotSettingsValidator psv,
        string deviceName,
        string? outputFilePath,
        bool isPlotToFile,
        ArgReader reader,
        bool isPdfExport)
    {
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Un tracé est déjà en cours dans AutoCAD.");

        var db = doc.Database;
        var layoutArg = reader.GetString("layout");

        // Résoudre l'ObjectId et le nom du layout
        string resolvedLayoutName;
        ObjectId layoutId;
        bool isModel;

        using (var tr = db.TransactionManager.StartTransaction())
        {
            var layoutsDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);

            if (string.IsNullOrWhiteSpace(layoutArg))
            {
                resolvedLayoutName = LayoutManager.Current.CurrentLayout;
                layoutId = layoutsDict.GetAt(resolvedLayoutName);
            }
            else if (string.Equals(layoutArg, "model", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(layoutArg, "objet", StringComparison.OrdinalIgnoreCase))
            {
                resolvedLayoutName = "Model";
                layoutId = layoutsDict.GetAt("Model");
            }
            else
            {
                var match = layoutsDict.Cast<DictionaryEntry>().FirstOrDefault(
                    e => string.Equals((string)e.Key, layoutArg, StringComparison.OrdinalIgnoreCase));

                if (match.Key is null)
                    throw new PipeException(PipeErrorCodes.InvalidParams, $"La présentation « {layoutArg} » n'existe pas dans ce dessin.");

                resolvedLayoutName = (string)match.Key;
                layoutId = (ObjectId)match.Value!;
            }

            var layoutObj = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
            isModel = layoutObj.ModelType;
            tr.Commit();
        }

        // Le moteur de tracé n'accepte que la présentation courante (sinon eLayoutNotCurrent) : elle est activée le
        // temps du tracé, puis l'utilisateur retrouve la sienne, même en cas d'échec.
        var layoutManager = LayoutManager.Current;
        var previousLayout = layoutManager.CurrentLayout;
        var activate = !string.Equals(previousLayout, resolvedLayoutName, StringComparison.OrdinalIgnoreCase);
        if (activate)
            layoutManager.CurrentLayout = resolvedLayoutName;

        try
        {
            return PlotLayout(doc, psv, deviceName, outputFilePath, isPlotToFile, reader, layoutId, resolvedLayoutName, isModel,
                activate ? previousLayout : null);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"AutoCAD a refusé le tracé de « {resolvedLayoutName} » vers « {deviceName} » ({ex.ErrorStatus}). " +
                "Vérifiez le format de papier (list_plot_devices), la zone de tracé et l'échelle.");
        }
        finally
        {
            if (activate)
            {
                try
                {
                    layoutManager.CurrentLayout = previousLayout;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    Log.Error($"Retour à la présentation « {previousLayout} » après le tracé", ex);
                }
            }
        }
    }

    private static object PlotLayout(
        Document doc,
        PlotSettingsValidator psv,
        string deviceName,
        string? outputFilePath,
        bool isPlotToFile,
        ArgReader reader,
        ObjectId layoutId,
        string resolvedLayoutName,
        bool isModel,
        string? activatedFrom)
    {
        var db = doc.Database;

        // Si tracé vers fichier, préparer le dossier
        if (isPlotToFile && !string.IsNullOrWhiteSpace(outputFilePath))
        {
            var dir = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Vérifier que le fichier n'est pas verrouillé
            if (File.Exists(outputFilePath))
            {
                try
                {
                    using var testStream = File.Open(outputFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException ex)
                {
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"Impossible d'écrire dans le fichier « {outputFilePath} » : il est peut-être ouvert dans une autre application ({ex.Message}).");
                }
            }
        }

        // Configurer PlotSettings
        using var plotSettings = new PlotSettings(isModel);
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var layoutObj = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
            plotSettings.CopyFrom(layoutObj);
            tr.Commit();
        }

        // Définir le périphérique ; le format de la présentation est retrouvé sur le nouveau périphérique si aucun
        // format n'est demandé (sinon le pilote PDF impose son format par défaut, souvent américain).
        var layoutMedia = plotSettings.CanonicalMediaName;
        var layoutPaper = plotSettings.PlotPaperSize;
        psv.SetPlotConfigurationName(plotSettings, deviceName, null);
        psv.RefreshLists(plotSettings);

        // Déterminer la zone de tracé (PlotType)
        var plotAreaArg = reader.GetString("plotArea");
        var isLayout = !isModel && (string.IsNullOrWhiteSpace(plotAreaArg) ||
                       plotAreaArg.Trim().Equals("layout", StringComparison.OrdinalIgnoreCase) ||
                       plotAreaArg.Trim().Equals("présentation", StringComparison.OrdinalIgnoreCase) ||
                       plotAreaArg.Trim().Equals("presentation", StringComparison.OrdinalIgnoreCase));

        // Définir le format de papier
        var paperSizeArg = reader.GetString("paperSize");
        var orientationArg = reader.GetString("orientation");
        ConfigurePaperAndOrientation(psv, plotSettings, paperSizeArg, orientationArg, layoutMedia, layoutPaper, isLayout);

        ConfigurePlotArea(psv, plotSettings, isModel, plotAreaArg, reader);

        // Définir l'échelle
        ConfigureScale(psv, plotSettings, db, isModel, plotAreaArg, reader);

        // Centrage
        var centerArg = reader.GetBool("center", fallback: true);
        if (plotSettings.PlotType != Autodesk.AutoCAD.DatabaseServices.PlotType.Layout)
        {
            try
            {
                psv.SetPlotCentered(plotSettings, centerArg);
            }
            catch
            {
                // Certains périphériques restreignent le centrage
            }
        }

        // Table des styles de tracé (CTB/STB)
        var plotStyleArg = reader.GetString("plotStyle");
        if (!string.IsNullOrWhiteSpace(plotStyleArg))
        {
            ConfigurePlotStyle(psv, plotSettings, plotStyleArg);
        }

        // Nombre de copies
        var copies = reader.GetInt("copies", fallback: 1, min: 1, max: 99);

        // Désactiver le tracé en tâche de fond pour garantir la synchronisation
        short originalBgPlot = 0;
        var bgPlotModified = false;
        try
        {
            var currentBg = Application.GetSystemVariable("BACKGROUNDPLOT");
            if (currentBg is short s && s != 0)
            {
                originalBgPlot = s;
                Application.SetSystemVariable("BACKGROUNDPLOT", (short)0);
                bgPlotModified = true;
            }
        }
        catch { }

        try
        {
            using var plotInfo = new PlotInfo { Layout = layoutId, OverrideSettings = plotSettings };
            using var validator = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
            validator.Validate(plotInfo);

            using var engine = PlotFactory.CreatePublishEngine();
            engine.BeginPlot(null, null);

            var docPlotName = Path.GetFileName(doc.Name);
            if (string.IsNullOrEmpty(docPlotName))
                docPlotName = "Drawing";

            engine.BeginDocument(plotInfo, docPlotName, null, copies, isPlotToFile, outputFilePath);

            using var pageInfo = new PlotPageInfo();
            engine.BeginPage(pageInfo, plotInfo, true, null);
            engine.BeginGenerateGraphics(null);
            engine.EndGenerateGraphics(null);
            engine.EndPage(null);
            engine.EndDocument(null);
            engine.EndPlot(null);
        }
        finally
        {
            if (bgPlotModified)
            {
                try
                {
                    Application.SetSystemVariable("BACKGROUNDPLOT", originalBgPlot);
                }
                catch { }
            }
        }

        long fileSizeBytes = 0;
        if (isPlotToFile && !string.IsNullOrWhiteSpace(outputFilePath) && File.Exists(outputFilePath))
        {
            try
            {
                fileSizeBytes = new FileInfo(outputFilePath).Length;
            }
            catch { }
        }

        return new
        {
            Success = true,
            Device = deviceName,
            Layout = resolvedLayoutName,
            IsModel = isModel,
            PlotArea = plotSettings.PlotType.ToString(),
            PaperSize = plotSettings.CanonicalMediaName,
            PaperWidth = Format.Number(plotSettings.PlotPaperSize.X),
            PaperHeight = Format.Number(plotSettings.PlotPaperSize.Y),
            Rotation = plotSettings.PlotRotation.ToString(),
            PlotToFile = isPlotToFile,
            FilePath = isPlotToFile ? outputFilePath : null,
            FileSizeBytes = fileSizeBytes > 0 ? fileSizeBytes : (long?)null,
            FileSizeKb = fileSizeBytes > 0 ? Math.Round(fileSizeBytes / 1024.0, 1) : (double?)null,
            Scale = plotSettings.UseStandardScale
                ? plotSettings.StdScaleType.ToString()
                : string.Create(CultureInfo.InvariantCulture,
                    $"{plotSettings.CustomPrintScale.Numerator:0.####} {plotSettings.PlotPaperUnits} = {plotSettings.CustomPrintScale.Denominator:0.####} unité(s) du dessin"),
            Copies = copies,
            PlotStyle = string.IsNullOrEmpty(plotSettings.CurrentStyleSheet) ? null : plotSettings.CurrentStyleSheet,
            // Présentation activée le temps du tracé, puis rétablie.
            TemporarilyActivatedFrom = activatedFrom,
        };
    }

    private static void ConfigurePaperAndOrientation(
        PlotSettingsValidator psv,
        PlotSettings plotSettings,
        string? paperSize,
        string? orientation,
        string? layoutMedia,
        Point2d layoutPaper,
        bool isLayout)
    {
        var canonicalMediaList = psv.GetCanonicalMediaNameList(plotSettings);
        if (canonicalMediaList.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(paperSize))
        {
            if (MatchLayoutMedia(canonicalMediaList, layoutMedia, layoutPaper) is { } kept &&
                !string.Equals(kept, plotSettings.CanonicalMediaName, StringComparison.Ordinal))
                psv.SetCanonicalMediaName(plotSettings, kept);
        }
        else
        {
            var matchedCanonical = FindMediaName(psv, plotSettings, canonicalMediaList, paperSize.Trim(), orientation, isLayout);
            if (matchedCanonical is not null)
            {
                psv.SetCanonicalMediaName(plotSettings, matchedCanonical);
            }
            else
            {
                // Construire la liste des 10 premiers formats pour aider l'utilisateur
                var sample = canonicalMediaList.Cast<string>().Take(10).ToList();
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Format de papier « {paperSize} » introuvable pour ce périphérique. Exemples reconnus : {string.Join(", ", sample)}.");
            }
        }

        // Orientation
        if (isLayout)
        {
            // CRUCIAL : Pour PlotType.Layout, la rotation 90° tourne le repère papier autour de l'origine (0,0)
            // sans translation de l'origine : les coordonnées X > 0 deviennent négatives et tout le tracé
            // est projeté hors de la feuille imprimable !
            // L'orientation paysage ou portrait doit être assurée exclusivement par le format canonique.
            psv.SetPlotRotation(plotSettings, PlotRotation.Degrees000);
        }
        else if (!string.IsNullOrWhiteSpace(orientation))
        {
            var isLandscape = string.Equals(orientation, "landscape", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(orientation, "paysage", StringComparison.OrdinalIgnoreCase);
            var isPortrait = string.Equals(orientation, "portrait", StringComparison.OrdinalIgnoreCase);

            if (isLandscape || isPortrait)
            {
                var paperSizePt = plotSettings.PlotPaperSize;
                // Si la largeur papier >= hauteur, la feuille est intrinsèquement orientée paysage
                var sheetIsLandscape = paperSizePt.X >= paperSizePt.Y;

                if (isLandscape)
                    psv.SetPlotRotation(plotSettings, sheetIsLandscape ? PlotRotation.Degrees000 : PlotRotation.Degrees090);
                else
                    psv.SetPlotRotation(plotSettings, sheetIsLandscape ? PlotRotation.Degrees090 : PlotRotation.Degrees000);
            }
        }
    }

    /// <summary>
    /// Format de la présentation sur le nouveau périphérique : même nom canonique, sinon mêmes dimensions (au
    /// millimètre près, dans un sens ou dans l'autre, en préférant le plein format sans marges et la même orientation). Null : format du pilote.
    /// </summary>
    private static string? MatchLayoutMedia(StringCollection canonicalList, string? layoutMedia, Point2d layoutPaper)
    {
        var names = canonicalList.Cast<string>().ToList();
        if (!string.IsNullOrEmpty(layoutMedia) && names.FirstOrDefault(n => string.Equals(n, layoutMedia, StringComparison.OrdinalIgnoreCase)) is { } same)
            return same;

        if (layoutPaper.X < 1 || layoutPaper.Y < 1)
            return null;

        bool SameSize(string name) => MediaSizeMm(name) is { } size &&
            ((Math.Abs(size.Width - layoutPaper.X) < 1 && Math.Abs(size.Height - layoutPaper.Y) < 1) ||
             (Math.Abs(size.Width - layoutPaper.Y) < 1 && Math.Abs(size.Height - layoutPaper.X) < 1));

        var candidates = names.Where(SameSize).ToList();
        if (candidates.Count == 0)
            return null;

        // Préférer d'abord les formats ayant exactement la même orientation que la feuille du Layout
        var sameOrientation = candidates.Where(n => MediaSizeMm(n) is { } size &&
            Math.Abs(size.Width - layoutPaper.X) < 1 && Math.Abs(size.Height - layoutPaper.Y) < 1).ToList();

        var pool = sameOrientation.Count > 0 ? sameOrientation : candidates;
        return pool.FirstOrDefault(n => n.Contains("full_bleed", StringComparison.OrdinalIgnoreCase)) ?? pool.FirstOrDefault();
    }

    /// <summary>Dimensions lues dans un nom canonique, par exemple « ISO_full_bleed_A3_(420.00_x_297.00_MM) ».</summary>
    internal static (double Width, double Height)? MediaSizeMm(string canonical)
    {
        var match = System.Text.RegularExpressions.Regex.Match(canonical,
            @"\((\d+(?:\.\d+)?)_x_(\d+(?:\.\d+)?)_(MM|Inches|Pixels)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || match.Groups[3].Value.Equals("Pixels", StringComparison.OrdinalIgnoreCase))
            return null;

        var factor = match.Groups[3].Value.Equals("MM", StringComparison.OrdinalIgnoreCase) ? 1.0 : 25.4;
        return (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * factor,
                double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * factor);
    }

    internal static string? FindMediaName(
        PlotSettingsValidator psv,
        PlotSettings plotSettings,
        StringCollection canonicalList,
        string requested,
        string? orientation = null,
        bool isLayout = false)
    {
        // 1. Correspondance exacte sur le nom canonique
        foreach (var item in canonicalList)
        {
            if (item is string name && string.Equals(name, requested, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        // 2. Correspondance exacte sur le nom localisé (ex: "ISO A4 (210 x 297 mm)")
        foreach (var item in canonicalList)
        {
            if (item is string canonical)
            {
                var local = psv.GetLocaleMediaName(plotSettings, canonical);
                if (string.Equals(local, requested, StringComparison.OrdinalIgnoreCase))
                    return canonical;
            }
        }

        // 3. Correspondance raccourcie pour A4, A3, A2, A1, A0, Letter, Legal, Tabloid
        var standardSizes = new[] { "A4", "A3", "A2", "A1", "A0", "Letter", "Legal", "Tabloid" };
        var matchedStandard = standardSizes.FirstOrDefault(s => string.Equals(s, requested, StringComparison.OrdinalIgnoreCase));

        if (matchedStandard is not null)
        {
            // Déterminer l'orientation souhaitée :
            // Si explicitement fournie, on la respecte.
            // Si omise : A3, A2, A1, A0 sont par défaut en paysage (largeur > hauteur) ; A4 est en portrait sauf si spécifié paysage.
            bool isLandscape;
            if (!string.IsNullOrWhiteSpace(orientation))
            {
                isLandscape = string.Equals(orientation, "landscape", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(orientation, "paysage", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                isLandscape = !string.Equals(matchedStandard, "A4", StringComparison.OrdinalIgnoreCase);
            }

            var token = new System.Text.RegularExpressions.Regex($@"(^|[_\s(]){matchedStandard}([_\s)]|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var candidates = new List<string>();
            foreach (var item in canonicalList)
            {
                if (item is string canonical && token.IsMatch(canonical))
                    candidates.Add(canonical);
            }

            if (candidates.Count > 0)
            {
                // Préférer d'abord les formats dont la largeur et la hauteur correspondent à l'orientation désirée
                var orientedCandidates = candidates.Where(c =>
                {
                    var size = MediaSizeMm(c);
                    if (size is null) return false;
                    return isLandscape ? (size.Value.Width >= size.Value.Height) : (size.Value.Width <= size.Value.Height);
                }).ToList();

                var pool = orientedCandidates.Count > 0 ? orientedCandidates : candidates;

                // Priorité 1 : plein format sans marges (ISO_full_bleed)
                var fullBleed = pool.FirstOrDefault(c => c.StartsWith("ISO_full_bleed", StringComparison.OrdinalIgnoreCase));
                if (fullBleed is not null) return fullBleed;

                // Priorité 2 : ISO_expand
                var expand = pool.FirstOrDefault(c => c.StartsWith("ISO_expand", StringComparison.OrdinalIgnoreCase));
                if (expand is not null) return expand;

                // Priorité 3 : ISO standard
                var iso = pool.FirstOrDefault(c => c.StartsWith("ISO", StringComparison.OrdinalIgnoreCase));
                if (iso is not null) return iso;

                return pool.First();
            }
        }

        // 4. Recherche partielle
        foreach (var item in canonicalList)
        {
            if (item is string canonical)
            {
                if (canonical.Contains(requested, StringComparison.OrdinalIgnoreCase))
                    return canonical;

                var local = psv.GetLocaleMediaName(plotSettings, canonical);
                if (local.Contains(requested, StringComparison.OrdinalIgnoreCase))
                    return canonical;
            }
        }

        return null;
    }

    private static void ConfigurePlotArea(
        PlotSettingsValidator psv,
        PlotSettings plotSettings,
        bool isModel,
        string? plotArea,
        ArgReader reader)
    {
        var area = (plotArea ?? (isModel ? "extents" : "layout")).Trim().ToLowerInvariant();

        switch (area)
        {
            case "extents":
            case "étendue":
            case "etendue":
                psv.SetPlotType(plotSettings, Autodesk.AutoCAD.DatabaseServices.PlotType.Extents);
                break;

            case "layout":
            case "présentation":
            case "presentation":
                if (isModel)
                    psv.SetPlotType(plotSettings, Autodesk.AutoCAD.DatabaseServices.PlotType.Extents);
                else
                    psv.SetPlotType(plotSettings, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                break;

            case "display":
            case "affichage":
                psv.SetPlotType(plotSettings, Autodesk.AutoCAD.DatabaseServices.PlotType.Display);
                break;

            case "window":
            case "fenêtre":
            case "fenetre":
                if (!reader.TryGet("window", out var winElem) || winElem.ValueKind != JsonValueKind.Array)
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        "Le paramètre « window » [minX, minY, maxX, maxY] est obligatoire lorsque plotArea est « window ».");

                var winArray = winElem.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                if (winArray.Length != 4)
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        "Le paramètre « window » doit contenir exactement 4 coordonnées numériques : [minX, minY, maxX, maxY].");

                var minPt = new Point2d(Math.Min(winArray[0], winArray[2]), Math.Min(winArray[1], winArray[3]));
                var maxPt = new Point2d(Math.Max(winArray[0], winArray[2]), Math.Max(winArray[1], winArray[3]));

                if (Math.Abs(maxPt.X - minPt.X) < 1e-6 || Math.Abs(maxPt.Y - minPt.Y) < 1e-6)
                    throw new PipeException(PipeErrorCodes.InvalidParams, "La fenêtre spécifiée a une largeur ou hauteur nulle.");

                psv.SetPlotWindowArea(plotSettings, new Extents2d(minPt, maxPt));
                psv.SetPlotType(plotSettings, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
                break;

            case "limits":
            case "limites":
                psv.SetPlotType(plotSettings, Autodesk.AutoCAD.DatabaseServices.PlotType.Limits);
                break;

            default:
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Zone de tracé « {plotArea} » inconnue. Valeurs acceptées : extents, layout, window, display, limits.");
        }
    }

    private static void ConfigureScale(
        PlotSettingsValidator psv,
        PlotSettings plotSettings,
        Database database,
        bool isModel,
        string? plotArea,
        ArgReader reader)
    {
        var hasScale = reader.TryGet("scale", out var scaleElem);
        var hasFit = reader.TryGet("fitToPaper", out _);
        var fitToPaper = reader.GetBool("fitToPaper", fallback: false);

        if (hasScale)
        {
            // La valeur calculée est en millimètres de papier par unité du dessin : le papier doit être en millimètres,
            // sinon un pilote en pouces fausse l'échelle d'un facteur 25,4.
            var customScaleValue = ParseScaleValue(database, scaleElem);
            psv.SetPlotPaperUnits(plotSettings, PlotPaperUnit.Millimeters);
            psv.SetUseStandardScale(plotSettings, false);
            psv.SetCustomPrintScale(plotSettings, new CustomScale(customScaleValue, 1.0));
            return;
        }

        if (hasFit && fitToPaper)
        {
            psv.SetUseStandardScale(plotSettings, true);
            psv.SetStdScaleType(plotSettings, StdScaleType.ScaleToFit);
            return;
        }

        // Par défaut :
        // Pour les présentations papier (layout) : échelle standard 1:1
        // Pour l'espace objet ou si l'utilisateur trace l'étendue : ajuster au papier (fit to paper)
        if (!isModel && plotSettings.PlotType == Autodesk.AutoCAD.DatabaseServices.PlotType.Layout)
        {
            psv.SetUseStandardScale(plotSettings, true);
            psv.SetStdScaleType(plotSettings, StdScaleType.StdScale1To1);
        }
        else
        {
            psv.SetUseStandardScale(plotSettings, true);
            psv.SetStdScaleType(plotSettings, StdScaleType.ScaleToFit);
        }
    }

    private static double ParseScaleValue(Database database, JsonElement scaleElement)
    {
        if (scaleElement.ValueKind == JsonValueKind.Number)
        {
            var val = scaleElement.GetDouble();
            if (val <= 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "L'échelle doit être un nombre strictement positif.");
            return val;
        }

        if (scaleElement.ValueKind != JsonValueKind.String)
            throw new PipeException(PipeErrorCodes.InvalidParams, "L'échelle attend un nombre ou une chaîne de ratio (ex: 1, \"1:500\", \"1/1000\").");

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
            $"Format d'échelle invalide « {text} ». Utilisez par exemple \"1:500\", \"1/1000\" ou 1.0.");
    }

    private static void ConfigurePlotStyle(PlotSettingsValidator psv, PlotSettings plotSettings, string plotStyleName)
    {
        var styleSheets = psv.GetPlotStyleSheetList();
        var candidate = plotStyleName.Trim();

        string? matched = null;
        foreach (var item in styleSheets)
        {
            if (item is string sheet && string.Equals(sheet, candidate, StringComparison.OrdinalIgnoreCase))
            {
                matched = sheet;
                break;
            }
        }

        if (matched is null && !candidate.EndsWith(".ctb", StringComparison.OrdinalIgnoreCase) && !candidate.EndsWith(".stb", StringComparison.OrdinalIgnoreCase))
        {
            var withCtb = candidate + ".ctb";
            foreach (var item in styleSheets)
            {
                if (item is string sheet && string.Equals(sheet, withCtb, StringComparison.OrdinalIgnoreCase))
                {
                    matched = sheet;
                    break;
                }
            }
        }

        if (matched is null)
        {
            var sample = styleSheets.Cast<string>().Take(10);
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Table de styles de tracé « {plotStyleName} » introuvable. Exemples disponibles : {string.Join(", ", sample)}.");
        }

        psv.SetCurrentStyleSheet(plotSettings, matched);
    }

    private static string ResolvePdfDevice(StringCollection deviceList, string? preferredDevice)
    {
        var devices = new List<string>(deviceList.Count);
        foreach (var d in deviceList)
        {
            if (d is string s)
                devices.Add(s);
        }

        if (!string.IsNullOrWhiteSpace(preferredDevice))
        {
            var found = FindDevice(devices, preferredDevice);
            if (found is not null)
                return found;
        }

        // 1. "AutoCAD PDF (General Documentation).pc3"
        var general = devices.FirstOrDefault(d => string.Equals(d, DefaultPdfDevice1, StringComparison.OrdinalIgnoreCase));
        if (general is not null)
            return general;

        // 2. "DWG To PDF.pc3"
        var dwgToPdf = devices.FirstOrDefault(d => string.Equals(d, DefaultPdfDevice2, StringComparison.OrdinalIgnoreCase));
        if (dwgToPdf is not null)
            return dwgToPdf;

        // 3. Tout PC3 contenant "PDF"
        var anyPdfPc3 = devices.FirstOrDefault(d => d.EndsWith(".pc3", StringComparison.OrdinalIgnoreCase) &&
                                                   d.Contains("PDF", StringComparison.OrdinalIgnoreCase));
        if (anyPdfPc3 is not null)
            return anyPdfPc3;

        // 4. Imprimante système contenant "PDF"
        var anyPdf = devices.FirstOrDefault(d => d.Contains("PDF", StringComparison.OrdinalIgnoreCase));
        if (anyPdf is not null)
            return anyPdf;

        return DefaultPdfDevice2;
    }

    private static string? FindDevice(List<string> devices, string query)
    {
        var q = query.Trim();
        var exact = devices.FirstOrDefault(d => string.Equals(d, q, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var pc3 = devices.FirstOrDefault(d => string.Equals(d, q + ".pc3", StringComparison.OrdinalIgnoreCase));
        if (pc3 is not null)
            return pc3;

        return devices.FirstOrDefault(d => d.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveOutputPdfPath(Document doc, string? layoutNameArg, string? targetPath)
    {
        if (!string.IsNullOrWhiteSpace(targetPath))
            return Path.GetFullPath(targetPath);

        return ResolveOutputPathWithExtension(doc, layoutNameArg, ".pdf");
    }

    private static string ResolveOutputPathWithExtension(Document doc, string? layoutNameArg, string extension)
    {
        string baseDir;
        string baseName;

        var docPath = doc.Name;
        if (!string.IsNullOrWhiteSpace(docPath) && Path.IsPathRooted(docPath) && File.Exists(docPath))
        {
            baseDir = Path.GetDirectoryName(docPath)!;
            baseName = Path.GetFileNameWithoutExtension(docPath);
        }
        else
        {
            baseDir = Path.GetTempPath();
            baseName = string.IsNullOrWhiteSpace(docPath) ? "Drawing" : Path.GetFileNameWithoutExtension(docPath);
        }

        var suffix = string.IsNullOrWhiteSpace(layoutNameArg)
            ? LayoutManager.Current.CurrentLayout
            : layoutNameArg;

        var safeSuffix = string.Join("_", suffix.Split(Path.GetInvalidFileNameChars()));
        var safeBase = string.Join("_", baseName.Split(Path.GetInvalidFileNameChars()));

        var fileName = $"{safeBase}_{safeSuffix}{extension}";
        return Path.Combine(baseDir, fileName);
    }
}
