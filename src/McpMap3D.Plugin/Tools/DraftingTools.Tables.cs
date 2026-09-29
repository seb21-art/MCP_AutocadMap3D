using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Tableaux (ACAD_TABLE) : titre fusionné, ligne d'en-têtes et lignes de données, largeurs de colonnes estimées
/// d'après le contenu. Les cellules se modifient ensuite avec edit_text.
/// </summary>
internal static partial class DraftingTools
{
    private const int MaxTableRows = 1000;
    private const int MaxTableColumns = 100;

    // Hauteur du titre rapportée à celle des autres cellules (2,5 donne 3,5).
    private const double TitleScale = 1.4;

    // Largeur moyenne d'un caractère rapportée à la hauteur du texte : large, pour qu'une valeur tienne sur une ligne.
    private const double CharacterWidth = 0.75;

    public static object CreateTable(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var position = reader.RequirePoint("position");
        var title = reader.GetString("title");
        var headers = reader.GetStrings("headers");
        var data = ReadCellRows(reader, "rows");
        var columns = Math.Max(headers.Count, data.Select(row => row.Count).DefaultIfEmpty(0).Max());
        if (columns == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Indiquez le contenu du tableau : « rows » (lignes de valeurs) et/ou « headers » (en-têtes de colonnes).");

        var titleRows = string.IsNullOrEmpty(title) ? 0 : 1;
        var headerRows = headers.Count > 0 ? 1 : 0;
        var rowCount = titleRows + headerRows + data.Count;
        if (columns > MaxTableColumns || rowCount > MaxTableRows)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Tableau limité à {MaxTableRows} lignes et {MaxTableColumns} colonnes ({rowCount} × {columns} demandées).");

        var textHeight = reader.Has("textHeight") ? Positive(reader, "textHeight") : 2.5;
        var titleHeight = textHeight * TitleScale;
        var rowHeight = reader.Has("rowHeight") ? Positive(reader, "rowHeight") : (double?)null;
        var widths = ReadColumnWidths(reader, columns);
        var alignments = reader.GetStrings("alignment").Select(ParseCellAlignment).ToList();
        if (alignments.Count > 1 && alignments.Count != columns)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"« alignment » : une valeur pour toutes les colonnes, ou une par colonne ({columns}).");

        var textStyleId = reader.Has("textStyle") ? TextStyle(context, reader.GetString("textStyle")!) : (ObjectId?)null;
        var styleId = TableStyleId(context, reader.GetString("style"));

        // Texte de chaque ligne, titre compris ; les cellules manquantes d'une ligne courte restent vides.
        var grid = new List<string[]>();
        if (titleRows == 1)
            grid.Add([title!, .. Enumerable.Repeat("", columns - 1)]);
        if (headerRows == 1)
            grid.Add(Pad(headers, columns));
        grid.AddRange(data.Select(row => Pad(row, columns)));

        var table = new Table();
        table.SetDatabaseDefaults(database);
        if (!styleId.IsNull)
            table.TableStyle = styleId;
        table.Position = position;
        table.SetSize(rowCount, columns);
        var appended = EditTools.Append(context, table, reader);

        for (var row = 0; row < rowCount; row++)
        {
            try
            {
                table.Rows[row].Style = row < titleRows ? "_TITLE" : row < titleRows + headerRows ? "_HEADER" : "_DATA";
            }
            catch (AcException)
            {
                // Style de tableau personnalisé sans ce style de cellule : la ligne garde celui d'AutoCAD.
            }
        }

        // Selon le style, AutoCAD fusionne d'office la première ligne, prévue pour un titre : on la défait,
        // puis on fusionne le titre demandé.
        try
        {
            table.UnmergeCells(table.Rows[0]);
        }
        catch (AcException)
        {
            // Première ligne non fusionnée.
        }

        if (titleRows == 1 && columns > 1)
            table.MergeCells(CellRange.Create(table, 0, 0, 0, columns - 1));

        if (textStyleId is ObjectId textStyle)
            table.Cells.TextStyleId = textStyle;

        // Le titre n'occupe que la première cellule de sa ligne fusionnée : les suivantes sont vides dans la grille.
        for (var row = 0; row < rowCount; row++)
        {
            table.Rows[row].TextHeight = row < titleRows ? titleHeight : textHeight;
            for (var column = 0; column < columns; column++)
            {
                if (grid[row][column].Length > 0)
                    table.Cells[row, column].TextString = EscapeMText(grid[row][column]);
            }
        }

        var firstDataRow = titleRows + headerRows;
        if (alignments.Count > 0 && data.Count > 0)
        {
            for (var column = 0; column < columns; column++)
                CellRange.Create(table, firstDataRow, column, rowCount - 1, column).Alignment =
                    alignments.Count == 1 ? alignments[0] : alignments[column];
        }

        var tableStyle = (TableStyle)transaction.GetObject(table.TableStyle, OpenMode.ForRead);
        var horizontalMargins = StyleMargin(tableStyle, CellMargins.Left, textHeight) + StyleMargin(tableStyle, CellMargins.Right, textHeight);
        var verticalMargins = StyleMargin(tableStyle, CellMargins.Top, textHeight) + StyleMargin(tableStyle, CellMargins.Bottom, textHeight);
        widths ??= EstimateColumnWidths(grid, titleRows, textHeight, titleHeight, horizontalMargins);
        for (var column = 0; column < columns; column++)
            table.Columns[column].Width = widths[column];

        // AutoCAD agrandit ensuite les lignes dont le texte ne tient pas (retours à la ligne).
        for (var row = 0; row < rowCount; row++)
            table.Rows[row].Height = rowHeight ?? (row < titleRows ? titleHeight : textHeight) + verticalMargins;

        table.GenerateLayout();
        table.RecomputeTableBlock(true);

        return new
        {
            Table = appended,
            Rows = rowCount,
            Columns = columns,
            TitleRow = titleRows == 1 ? 0 : (int?)null,
            HeaderRow = headerRows == 1 ? titleRows : (int?)null,
            FirstDataRow = data.Count > 0 ? firstDataRow : (int?)null,
            Width = Format.Number(table.Width),
            Height = Format.Number(table.Height),
            Style = tableStyle.Name,
            TextHeight = Format.Number(textHeight),
            Position = Format.Point(table.Position),
            Extents = Format.Extents(Format.TryGetExtents(table)),
        };
    }

    /// <summary>
    /// Modifie des cellules d'un tableau (edit_text). Une ligne au-delà de la dernière ajoute les lignes manquantes,
    /// au format de la dernière ligne.
    /// </summary>
    internal static object EditTableCells(Table table, ArgReader reader, double? height)
    {
        var edits = ReadCellEdits(reader);
        var rows = table.Rows.Count;
        var columns = table.Columns.Count;
        foreach (var (row, column, _) in edits)
        {
            if (column >= columns)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Colonne {column} hors du tableau, qui en compte {columns} (numérotées à partir de 0).");
        }

        var lastRow = edits.Max(edit => edit.Row);
        if (lastRow >= MaxTableRows)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Tableau limité à {MaxTableRows} lignes.");

        var added = Math.Max(0, lastRow + 1 - rows);
        if (added > 0)
            AppendTableRows(table, rows, added);

        foreach (var (row, column, text) in edits)
        {
            var cell = table.Cells[row, column];
            cell.TextString = EscapeMText(text);
            if (height is double textHeight)
                cell.TextHeight = textHeight;
        }

        table.GenerateLayout();
        table.RecomputeTableBlock(true);

        return new
        {
            Handle = table.Handle.ToString(),
            Type = Format.DxfName(table),
            Rows = table.Rows.Count,
            Columns = columns,
            AddedRows = added > 0 ? added : (int?)null,
            Cells = edits.Select(edit => new { edit.Row, edit.Column, Text = CellText(table, edit.Row, edit.Column) }).ToList(),
            Width = Format.Number(table.Width),
            Height = Format.Number(table.Height),
        };
    }

    /// <summary>Texte des cellules pour list_entities, sans codes de mise en forme, dans la limite demandée.</summary>
    internal static (string[][] Cells, bool Truncated) TableTexts(Table table, int maximumRows, int maximumColumns, int maximumLength)
    {
        var rows = Math.Min(table.Rows.Count, maximumRows);
        var columns = Math.Min(table.Columns.Count, maximumColumns);
        var cells = new string[rows][];
        for (var row = 0; row < rows; row++)
        {
            cells[row] = new string[columns];
            for (var column = 0; column < columns; column++)
                cells[row][column] = Format.Truncate(CellText(table, row, column), maximumLength);
        }

        return (cells, rows < table.Rows.Count || columns < table.Columns.Count);
    }

    private static string CellText(Table table, int row, int column)
    {
        var cell = table.Cells[row, column];
        try
        {
            return cell.GetTextString(FormatOption.IgnoreMtextFormat) ?? "";
        }
        catch (AcException)
        {
            return cell.TextString ?? "";
        }
    }

    /// <summary>Lignes ajoutées en fin de tableau, au format de la dernière.</summary>
    private static void AppendTableRows(Table table, int rows, int added)
    {
        try
        {
            table.InsertRowsAndInherit(rows, rows - 1, added);
        }
        catch (AcException)
        {
            // Repli : agrandissement du tableau, puis recopie de la hauteur de texte et de ligne de la dernière ligne.
            var last = table.Rows[rows - 1];
            var textHeight = last.TextHeight;
            var height = last.Height;
            table.SetSize(rows + added, table.Columns.Count);
            for (var row = rows; row < rows + added; row++)
            {
                if (textHeight is double value)
                    table.Rows[row].TextHeight = value;
                table.Rows[row].Height = height;
            }
        }
    }

    private static ObjectId TableStyleId(ToolContext context, string? name)
    {
        var database = context.Database;
        if (string.IsNullOrWhiteSpace(name))
            return database.Tablestyle;

        var styles = (DBDictionary)context.RequireTransaction().GetObject(database.TableStyleDictionaryId, OpenMode.ForRead);
        if (styles.Contains(name))
            return styles.GetAt(name);

        var names = new List<string>();
        foreach (var entry in styles)
            names.Add(entry.Key);

        throw new PipeException(PipeErrorCodes.InvalidParams,
            $"Le style de tableau « {name} » n'existe pas. Styles du dessin : {string.Join(", ", names)}.");
    }

    /// <summary>Marge des cellules de données du style ; à défaut, 40 % de la hauteur du texte.</summary>
    private static double StyleMargin(TableStyle style, CellMargins side, double textHeight)
    {
        try
        {
            return style.Margin(side, "_DATA");
        }
        catch (AcException)
        {
            return textHeight * 0.4;
        }
    }

    /// <summary>
    /// Largeur de chaque colonne d'après sa plus longue ligne de texte (titre exclu), puis élargissement régulier si
    /// le titre ne tient pas dans la largeur totale.
    /// </summary>
    private static double[] EstimateColumnWidths(List<string[]> grid, int titleRows, double textHeight, double titleHeight, double margins)
    {
        var columns = grid[0].Length;
        var widths = new double[columns];
        for (var column = 0; column < columns; column++)
        {
            var longest = grid.Skip(titleRows).Select(row => LongestLine(row[column])).DefaultIfEmpty(0).Max();
            widths[column] = Math.Max(longest, 2) * CharacterWidth * textHeight + margins;
        }

        if (titleRows == 1)
        {
            var needed = LongestLine(grid[0][0]) * CharacterWidth * titleHeight + margins;
            var total = widths.Sum();
            if (needed > total)
            {
                for (var column = 0; column < columns; column++)
                    widths[column] += (needed - total) / columns;
            }
        }

        return widths;
    }

    private static int LongestLine(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Max(line => line.Length);

    private static string[] Pad(IReadOnlyList<string> values, int columns) =>
        [.. values, .. Enumerable.Repeat("", columns - values.Count)];

    /// <summary>Largeurs de colonnes : une valeur pour toutes, ou une par colonne ; null si absentes.</summary>
    private static double[]? ReadColumnWidths(ArgReader reader, int columns)
    {
        if (!reader.TryGet("columnWidths", out var value))
            return null;

        var widths = value.ValueKind switch
        {
            JsonValueKind.Number => [value.GetDouble()],
            JsonValueKind.Array when value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Number) =>
                value.EnumerateArray().Select(item => item.GetDouble()).ToArray(),
            _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                "« columnWidths » doit être un nombre ou un tableau de nombres."),
        };

        if (widths.Length != 1 && widths.Length != columns)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"« columnWidths » : une largeur pour toutes les colonnes, ou une par colonne ({columns}).");

        if (widths.Any(width => width <= 0))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Les largeurs de colonnes doivent être strictement positives.");

        return widths.Length == 1 ? [.. Enumerable.Repeat(widths[0], columns)] : widths;
    }

    /// <summary>Lignes de valeurs : chaînes, nombres (écrits tels que reçus) ou null (cellule vide).</summary>
    private static List<List<string>> ReadCellRows(ArgReader reader, string name)
    {
        if (!reader.TryGet(name, out var value))
            return [];

        var invalid = new PipeException(PipeErrorCodes.InvalidParams,
            $"Le paramètre « {name} » doit être un tableau de lignes, par exemple [[\"P1\", \"12,5\"], [\"P2\", 8]].");
        if (value.ValueKind != JsonValueKind.Array)
            throw invalid;

        var rows = new List<List<string>>();
        foreach (var row in value.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
                throw invalid;

            rows.Add(row.EnumerateArray().Select(item => CellValue(item) ?? throw invalid).ToList());
        }

        return rows;
    }

    private static string? CellValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        JsonValueKind.Null => "",
        _ => null,
    };

    /// <summary>Cellules à modifier : [{row, column, text}], lignes et colonnes comptées à partir de 0.</summary>
    private static List<(int Row, int Column, string Text)> ReadCellEdits(ArgReader reader)
    {
        var invalid = new PipeException(PipeErrorCodes.InvalidParams,
            "Pour un tableau, indiquez « cells », par exemple [{\"row\": 2, \"column\": 1, \"text\": \"12,5\"}] " +
            "(lignes et colonnes comptées à partir de 0, titre compris).");
        if (!reader.TryGet("cells", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
            throw invalid;

        var edits = new List<(int Row, int Column, string Text)>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("row", out var row) || !row.TryGetInt32(out var rowIndex) || rowIndex < 0
                || !item.TryGetProperty("column", out var column) || !column.TryGetInt32(out var columnIndex) || columnIndex < 0
                || !item.TryGetProperty("text", out var text) || CellValue(text) is not string content)
                throw invalid;

            edits.Add((rowIndex, columnIndex, content));
        }

        return edits;
    }

    private static CellAlignment ParseCellAlignment(string value) => value.Trim().ToLowerInvariant() switch
    {
        "top_left" => CellAlignment.TopLeft,
        "top_center" => CellAlignment.TopCenter,
        "top_right" => CellAlignment.TopRight,
        "middle_left" => CellAlignment.MiddleLeft,
        "middle_center" => CellAlignment.MiddleCenter,
        "middle_right" => CellAlignment.MiddleRight,
        "bottom_left" => CellAlignment.BottomLeft,
        "bottom_center" => CellAlignment.BottomCenter,
        "bottom_right" => CellAlignment.BottomRight,
        _ => throw new PipeException(PipeErrorCodes.InvalidParams,
            $"Alignement « {value} » inconnu : top_left, top_center, top_right, middle_left, middle_center, " +
            "middle_right, bottom_left, bottom_center ou bottom_right."),
    };
}
