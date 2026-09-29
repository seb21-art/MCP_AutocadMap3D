using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Styles de texte : police SHX ou TrueType, hauteur fixe, facteur de largeur, inclinaison. Les outils de texte,
/// de cotation et de tableau les désignent par leur nom.
/// </summary>
internal static class TextStyleTools
{
    public static object ListTextStyles(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var filter = NameFilter.Create(reader.GetStrings("names"));
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var table = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);

        var styles = new List<object>();
        foreach (var id in table)
        {
            var style = (TextStyleTableRecord)transaction.GetObject(id, OpenMode.ForRead);

            // Fichiers de formes (symboles des types de lignes complexes) : pas des styles de texte.
            if (style.IsShapeFile || string.IsNullOrEmpty(style.Name))
                continue;

            if (filter is null || filter.IsMatch(style.Name))
                styles.Add(Describe(style, database));
        }

        var current = (TextStyleTableRecord)transaction.GetObject(database.Textstyle, OpenMode.ForRead);
        return new { Count = styles.Count, Current = current.Name, Styles = styles };
    }

    /// <summary>Crée un style (copie d'un style existant) ou modifie un style, et le rend courant sur demande.</summary>
    public static object SetTextStyle(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var name = reader.RequireString("name").Trim();
        var table = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);

        var created = !table.Has(name);
        TextStyleTableRecord style;
        if (created)
        {
            try
            {
                SymbolUtilityServices.ValidateSymbolName(name, allowVerticalBar: false);
            }
            catch (AcException)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » n'est pas un nom de style valide.");
            }

            // Nouveau style : copie de basedOn, ou du style courant.
            var sourceId = reader.Has("basedOn") ? DraftingTools.TextStyle(context, reader.GetString("basedOn")!) : database.Textstyle;
            var source = (TextStyleTableRecord)transaction.GetObject(sourceId, OpenMode.ForRead);
            style = (TextStyleTableRecord)source.Clone();
            style.Name = name;
            table.UpgradeOpen();
            table.Add(style);
            transaction.AddNewlyCreatedDBObject(style, true);
        }
        else
        {
            if (reader.Has("basedOn"))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Le style « {name} » existe déjà : basedOn ne sert qu'à la création. Modifiez-le directement.");

            style = (TextStyleTableRecord)transaction.GetObject(table[name], OpenMode.ForWrite);
            if (style.IsShapeFile)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » est un fichier de formes, pas un style de texte.");
        }

        string? warning = null;
        if (reader.Has("font"))
            warning = ApplyFont(style, reader, database);
        else if (reader.Has("bold") || reader.Has("italic"))
            ApplyTrueTypeStyle(style, reader);

        if (reader.Has("bigFont"))
            style.BigFontFileName = reader.GetString("bigFont")!.Trim();

        if (reader.Has("height"))
        {
            var height = reader.RequireDouble("height");
            style.TextSize = height >= 0
                ? height
                : throw new PipeException(PipeErrorCodes.InvalidParams, "La hauteur fixe doit être positive, ou 0 pour une hauteur libre.");
        }

        if (reader.Has("widthFactor"))
        {
            var factor = reader.RequireDouble("widthFactor");
            style.XScale = factor is > 0 and <= 100
                ? factor
                : throw new PipeException(PipeErrorCodes.InvalidParams, "Le facteur de largeur doit être compris entre 0 (exclu) et 100.");
        }

        if (reader.Has("obliqueAngle"))
        {
            var angle = reader.RequireDouble("obliqueAngle");
            style.ObliquingAngle = angle is >= -85 and <= 85
                ? Format.Radians(angle)
                : throw new PipeException(PipeErrorCodes.InvalidParams, "L'angle d'inclinaison doit être compris entre -85 et 85 degrés.");
        }

        if (reader.GetBool("current", false))
            database.Textstyle = style.ObjectId;

        // Les textes existants du style changent de police au prochain regen.
        if (!created && reader.Has("font"))
            context.AfterCommit(() => context.Editor.Regen());

        return new
        {
            Created = created,
            Style = Describe(style, database),
            Warning = warning,
        };
    }

    private static object Describe(TextStyleTableRecord style, Database database)
    {
        var font = style.Font;
        var trueType = !string.IsNullOrEmpty(font.TypeFace);
        return new
        {
            style.Name,
            Current = style.ObjectId == database.Textstyle,
            Font = trueType ? font.TypeFace : style.FileName,
            FontFile = string.IsNullOrEmpty(style.FileName) ? null : style.FileName,
            TrueType = trueType || IsTrueTypeFile(style.FileName),
            Bold = trueType ? font.Bold : (bool?)null,
            Italic = trueType ? font.Italic : (bool?)null,
            BigFont = string.IsNullOrEmpty(style.BigFontFileName) ? null : style.BigFontFileName,

            // 0 : hauteur libre, choisie par chaque texte.
            Height = Format.Number(style.TextSize),
            WidthFactor = Format.Number(style.XScale),
            ObliqueAngle = Format.Degrees(style.ObliquingAngle),
        };
    }

    /// <summary>
    /// Police : fichier SHX (romans.shx), fichier TrueType (arial.ttf) ou nom de famille TrueType (Arial), avec gras
    /// et italique pour une famille. Renvoie un avertissement si AutoCAD ne trouve pas le fichier.
    /// </summary>
    private static string? ApplyFont(TextStyleTableRecord style, ArgReader reader, Database database)
    {
        var font = reader.RequireString("font").Trim();
        if (font.Length == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le paramètre « font » est vide.");

        var extension = Path.GetExtension(font).ToLowerInvariant();
        var bold = reader.GetBool("bold", false);
        var italic = reader.GetBool("italic", false);
        switch (extension)
        {
            case ".shx":
                if (bold || italic)
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        "Gras et italique ne s'appliquent qu'à une police TrueType désignée par son nom de famille (Arial…).");

                style.Font = new FontDescriptor("", false, false, 0, 0);
                style.FileName = font;
                return MissingFileWarning(font, database, FindFileHint.FontFile);

            case ".ttf" or ".ttc" or ".otf":
                if (bold || italic)
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        "Pour du gras ou de l'italique, désignez la police par son nom de famille (Arial…) plutôt que par son fichier.");

                style.Font = new FontDescriptor("", false, false, 0, 0);
                style.FileName = font;
                return MissingFileWarning(font, database, FindFileHint.TrueTypeFontFile);

            case "":
                // Nom de famille : AutoCAD retrouve la police installée par ce nom (jeu de caractères ANSI,
                // famille à chasse variable). Un ancien fichier SHX ne doit pas rester associé au style.
                if (!IsTrueTypeFile(style.FileName))
                    TryClearFileName(style);

                style.Font = new FontDescriptor(font, bold, italic, 0, 34);
                return null;

            default:
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Police « {font} » : indiquez un fichier .shx, .ttf, .ttc ou .otf, ou un nom de famille TrueType (Arial, Calibri…).");
        }
    }

    /// <summary>Gras ou italique seuls, sur un style déjà en police TrueType désignée par son nom.</summary>
    private static void ApplyTrueTypeStyle(TextStyleTableRecord style, ArgReader reader)
    {
        var font = style.Font;
        if (string.IsNullOrEmpty(font.TypeFace))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Le style « {style.Name} » n'utilise pas une police TrueType désignée par son nom : indiquez aussi « font » (Arial…).");

        style.Font = new FontDescriptor(font.TypeFace, reader.GetBool("bold", font.Bold), reader.GetBool("italic", font.Italic),
            font.CharacterSet, font.PitchAndFamily);
    }

    private static void TryClearFileName(TextStyleTableRecord style)
    {
        try
        {
            style.FileName = "";
        }
        catch (AcException)
        {
            // AutoCAD refuse un nom de fichier vide : le nom de famille reste prioritaire.
        }
    }

    private static bool IsTrueTypeFile(string? fileName) =>
        Path.GetExtension(fileName ?? "").ToLowerInvariant() is ".ttf" or ".ttc" or ".otf";

    private static string? MissingFileWarning(string fileName, Database database, FindFileHint hint)
    {
        try
        {
            if (!string.IsNullOrEmpty(HostApplicationServices.Current.FindFile(fileName, database, hint)))
                return null;
        }
        catch (AcException)
        {
            // Fichier introuvable : avertissement ci-dessous.
        }

        return $"AutoCAD ne trouve pas « {fileName} » dans ses chemins de recherche ni dans les polices de Windows : " +
               "le texte s'affichera avec une police de remplacement.";
    }
}
