using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Dessin technique 2D : cercles, arcs, rectangles, texte multiligne, hachures et cotations.
/// Coordonnées dans le repère général du dessin, angles en degrés, sens trigonométrique.
/// </summary>
internal static partial class DraftingTools
{
    public static object CreateCircle(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var circle = new Circle(reader.RequirePoint("center"), Vector3d.ZAxis, Positive(reader, "radius"));
        return EditTools.Append(context, circle, reader);
    }

    /// <summary>Arc par centre, rayon et angles, ou par trois points (départ, point intermédiaire, arrivée).</summary>
    public static object CreateArc(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        Arc arc;
        if (reader.Has("center"))
        {
            arc = new Arc(
                reader.RequirePoint("center"),
                Positive(reader, "radius"),
                Format.Radians(reader.RequireDouble("startAngle")),
                Format.Radians(reader.RequireDouble("endAngle")));
        }
        else if (reader.Has("start") && reader.Has("mid") && reader.Has("end"))
        {
            var (start, mid, end) = (reader.RequirePoint("start"), reader.RequirePoint("mid"), reader.RequirePoint("end"));

            // Points alignés : AutoCAD ne proteste pas, mais l'arc obtenu est dégénéré.
            var cross = (mid - start).CrossProduct(end - start).Length;
            if (cross <= 1e-9 * Math.Max(1, (mid - start).Length * (end - start).Length))
                throw new PipeException(PipeErrorCodes.InvalidParams, "Les trois points de l'arc sont alignés ou confondus.");

            CircularArc3d geometry;
            try
            {
                geometry = new CircularArc3d(start, mid, end);
            }
            catch (AcException)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams, "Les trois points de l'arc sont alignés ou confondus.");
            }

            // Un arc AutoCAD tourne toujours dans le sens trigonométrique : un arc horaire est pris à l'envers.
            var center = geometry.Center;
            var clockwise = geometry.Normal.Z < 0;
            var (from, to) = clockwise ? (end, start) : (start, end);
            arc = new Arc(center, geometry.Radius, Math.Atan2(from.Y - center.Y, from.X - center.X), Math.Atan2(to.Y - center.Y, to.X - center.X));
        }
        else
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Définissez l'arc soit par center, radius, startAngle et endAngle, soit par trois points start, mid et end.");
        }

        var result = EditTools.Append(context, arc, reader);
        return new
        {
            Arc = result,
            Center = Format.Point(arc.Center),
            Radius = Format.Number(arc.Radius),
            StartAngle = Format.Degrees(arc.StartAngle),
            EndAngle = Format.Degrees(arc.EndAngle),
            Length = Format.Number(arc.Length),
        };
    }

    /// <summary>
    /// Ellipse par centre, demi-grand axe, demi-petit axe et orientation du grand axe ; arc d'ellipse avec startAngle
    /// et endAngle, mesurés depuis le grand axe dans le sens trigonométrique.
    /// </summary>
    public static object CreateEllipse(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var center = reader.RequirePoint("center");
        var major = Positive(reader, "majorRadius");
        var minor = Positive(reader, "minorRadius");
        if (minor > major)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "« minorRadius » dépasse « majorRadius » : échangez-les et tournez l'ellipse de 90° avec « rotation ».");

        var rotation = Format.Radians(reader.GetDouble("rotation", 0));
        var hasStart = reader.Has("startAngle");
        if (hasStart != reader.Has("endAngle"))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Pour un arc d'ellipse, indiquez à la fois « startAngle » et « endAngle ».");

        var (startAngle, endAngle) = hasStart
            ? (Format.Radians(reader.RequireDouble("startAngle")), Format.Radians(reader.RequireDouble("endAngle")))
            : (0.0, 2 * Math.PI);
        if (hasStart && Math.Abs(Math.IEEERemainder(endAngle - startAngle, 2 * Math.PI)) < 1e-9)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "« startAngle » et « endAngle » sont confondus : omettez-les pour une ellipse complète.");

        var majorAxis = new Vector3d(Math.Cos(rotation), Math.Sin(rotation), 0) * major;
        var ellipse = new Ellipse(center, Vector3d.ZAxis, majorAxis, minor / major, startAngle, endAngle);
        var result = EditTools.Append(context, ellipse, reader);
        return new
        {
            Ellipse = result,
            Center = Format.Point(ellipse.Center),
            MajorRadius = Format.Number(major),
            MinorRadius = Format.Number(minor),
            Rotation = Format.Degrees(rotation),
            Closed = ellipse.Closed,
            Length = Format.Number(ellipse.GetDistanceAtParameter(ellipse.EndParam)),
            Area = ellipse.Closed ? Format.Number(ellipse.Area) : (double?)null,
        };
    }

    /// <summary>
    /// Spline passant par des points (lissage, comme SPLINE), ou définie par ses sommets de contrôle
    /// (controlPoints = true, degré 1 à 10). Ouverte ou fermée.
    /// </summary>
    public static object CreateSpline(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var controlPoints = reader.GetBool("controlPoints", false);
        var closed = reader.GetBool("closed", false);
        var points = reader.RequirePoints("points", minimum: closed ? 3 : 2);
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].DistanceTo(points[i - 1]) < 1e-9)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Les points {i - 1} et {i} sont confondus.");
        }

        var collection = new Point3dCollection([.. points]);
        Spline spline;
        try
        {
            if (controlPoints)
            {
                var degree = reader.GetInt("degree", 3, min: 1, max: 10);
                if (points.Count <= degree)
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"Une spline de degré {degree} demande au moins {degree + 1} sommets de contrôle.");

                spline = ControlSpline(points, degree, closed);
            }
            else
            {
                if (reader.Has("degree"))
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        "« degree » ne s'applique qu'aux sommets de contrôle (controlPoints = true) : une spline de lissage est de degré 3.");

                spline = closed
                    ? new Spline(collection, true, KnotParameterizationEnum.Chord, 3, 0)
                    : new Spline(collection, 4, 0);
            }
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu créer la spline ({ex.ErrorStatus}).");
        }

        var result = EditTools.Append(context, spline, reader);
        return new
        {
            Spline = result,
            Method = controlPoints ? "control" : "fit",
            spline.Degree,
            Closed = spline.Closed,
            Points = points.Count,
            Length = Format.Number(spline.GetDistanceAtParameter(spline.EndParam) - spline.GetDistanceAtParameter(spline.StartParam)),
        };
    }

    /// <summary>
    /// Spline non rationnelle par sommets de contrôle, à nœuds uniformes. Ouverte : nœuds bloqués aux extrémités,
    /// la courbe part du premier sommet et finit au dernier. Fermée : périodique, les premiers sommets sont repris
    /// à la fin pour que la courbe se referme sans angle.
    /// </summary>
    private static Spline ControlSpline(IReadOnlyList<Point3d> points, int degree, bool closed)
    {
        var controls = new Point3dCollection([.. points]);
        var knots = new DoubleCollection();
        if (closed)
        {
            for (var i = 0; i < degree; i++)
                controls.Add(points[i]);

            for (var i = 0; i < controls.Count + degree + 1; i++)
                knots.Add(i);
        }
        else
        {
            var spans = points.Count - degree;
            for (var i = 0; i < degree; i++)
                knots.Add(0);

            for (var i = 0; i <= spans; i++)
                knots.Add((double)i / spans);

            for (var i = 0; i < degree; i++)
                knots.Add(1);
        }

        return new Spline(degree, false, closed, closed, controls, knots, new DoubleCollection(), 0, 0);
    }

    public static object CreateRectangle(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var a = reader.RequirePoint("corner1");
        var b = reader.RequirePoint("corner2");
        if (Math.Abs(a.X - b.X) < 1e-9 || Math.Abs(a.Y - b.Y) < 1e-9)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Les deux coins doivent différer en X et en Y.");

        var polyline = new Polyline(4) { Closed = true, Elevation = a.Z };
        polyline.AddVertexAt(0, new Point2d(a.X, a.Y), 0, 0, 0);
        polyline.AddVertexAt(1, new Point2d(b.X, a.Y), 0, 0, 0);
        polyline.AddVertexAt(2, new Point2d(b.X, b.Y), 0, 0, 0);
        polyline.AddVertexAt(3, new Point2d(a.X, b.Y), 0, 0, 0);
        var result = EditTools.Append(context, polyline, reader);
        return new
        {
            Rectangle = result,
            Width = Format.Number(Math.Abs(b.X - a.X)),
            Height = Format.Number(Math.Abs(b.Y - a.Y)),
            Area = Format.Number(polyline.Area),
        };
    }

    public static object CreateMText(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var mtext = new MText
        {
            Location = reader.RequirePoint("position"),
            Contents = EscapeMText(reader.RequireString("text")),
            TextHeight = reader.GetDouble("height", 2.5),
            Width = reader.GetDouble("width", 0),
            Rotation = Format.Radians(reader.GetDouble("rotation", 0)),
            Attachment = ParseAttachment(reader.GetString("attachment", "top_left")!),
        };

        if (mtext.TextHeight <= 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "La hauteur du texte doit être supérieure à zéro.");

        if (reader.Has("style"))
            mtext.TextStyleId = TextStyle(context, reader.GetString("style")!);

        var appended = EditTools.Append(context, mtext, reader);

        // Masque d'arrière-plan : cadre plein derrière le texte, couleur du fond d'écran par défaut, qui cache les traits.
        if (reader.GetBool("backgroundMask", false))
        {
            mtext.BackgroundFill = true;
            mtext.UseBackgroundColor = !reader.Has("maskColor");
            if (reader.Has("maskColor"))
                mtext.BackgroundFillColor = EditTools.ParseColor(reader.GetString("maskColor")!);

            mtext.BackgroundScaleFactor = 1.5;
        }
        else if (reader.Has("maskColor"))
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, "« maskColor » s'utilise avec « backgroundMask »: true.");
        }

        return appended;
    }

    /// <summary>Hachure délimitée par des contours fermés existants (associative) ou par une liste de points.</summary>
    public static object CreateHatch(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var pattern = (reader.GetString("pattern") ?? "SOLID").Trim().ToUpperInvariant();
        var gradient = reader.GetString("gradient")?.Trim().ToUpperInvariant();
        if (gradient is not null && reader.Has("pattern"))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez soit un motif « pattern », soit un dégradé « gradient », pas les deux.");
        if (gradient is not null && !GradientNames.Contains(gradient))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Dégradé « {gradient} » inconnu : {string.Join(", ", GradientNames.Select(name => name.ToLowerInvariant()))}.");
        if (gradient is null && reader.Has("gradientColors"))
            throw new PipeException(PipeErrorCodes.InvalidParams, "« gradientColors » s'utilise avec « gradient ».");

        var hasBoundaries = reader.Has("boundaries");
        if (hasBoundaries == reader.Has("points"))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Indiquez le contour soit par « boundaries » (handles de contours fermés), soit par « points » (sommets).");

        var boundaryIds = hasBoundaries ? Handles.Resolve(context, reader, "boundaries") : [];
        foreach (var id in boundaryIds)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not Curve { Closed: true } and not Region)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Le contour {id.Handle} n'est pas une courbe fermée.");
        }

        var points = hasBoundaries ? [] : reader.RequirePoints("points", minimum: 3);

        var hatch = new Hatch();
        var appended = EditTools.Append(context, hatch, reader);

        if (gradient is not null)
        {
            // Dégradé de la première couleur vers la seconde (blanc par défaut), orienté par angle.
            hatch.HatchObjectType = HatchObjectType.GradientObject;
            hatch.SetGradient(GradientPatternType.PreDefinedGradient, gradient);
            hatch.GradientOneColorMode = false;
            hatch.SetGradientColors(ReadGradientColors(reader));
            hatch.GradientAngle = Format.Radians(reader.GetDouble("angle", 0));
        }
        else
        {
            // L'objet doit déjà être dans le dessin. AutoCAD refuse angle et échelle tant qu'aucun motif n'est défini,
            // et ne les prend en compte qu'au motif suivant : on applique donc le motif, puis on le réapplique.
            try
            {
                hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
            }
            catch (AcException)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Motif de hachure « {pattern} » inconnu. Exemples : SOLID (remplissage plein), ANSI31, ANSI37, " +
                    "AR-CONC, AR-SAND, GRAVEL, NET, DOTS.");
            }
        }

        if (gradient is null && pattern != "SOLID")
        {
            var scale = reader.GetDouble("scale", 1);
            if (scale <= 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "L'échelle du motif doit être strictement positive.");

            hatch.PatternAngle = Format.Radians(reader.GetDouble("angle", 0));
            hatch.PatternScale = scale;
            hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
        }

        if (hasBoundaries)
        {
            hatch.Associative = reader.GetBool("associative", true);
            foreach (var id in boundaryIds)
                hatch.AppendLoop(HatchLoopTypes.Default, new ObjectIdCollection([id]));
        }
        else
        {
            var vertices = new Point2dCollection(points.Select(point => new Point2d(point.X, point.Y)).ToArray());
            vertices.Add(vertices[0]);
            var bulges = new DoubleCollection(Enumerable.Repeat(0.0, vertices.Count).ToArray());
            hatch.Elevation = points[0].Z;
            hatch.AppendLoop(HatchLoopTypes.Polyline | HatchLoopTypes.External, vertices, bulges);
        }

        try
        {
            hatch.EvaluateHatch(true);
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"AutoCAD n'a pas pu calculer la hachure ({ex.ErrorStatus}) : vérifiez que les contours sont fermés et ne se recoupent pas.");
        }

        // L'aire d'une hachure associative n'est calculée qu'à la validation de la transaction : pour un contour
        // unique, c'est celle du contour ; sinon, elle se lit ensuite avec list_entities.
        var area = TryArea(hatch);
        if (area is null && boundaryIds.Count == 1)
        {
            area = transaction.GetObject(boundaryIds[0], OpenMode.ForRead) switch
            {
                Region region => Format.Number(region.Area),
                Curve curve => Format.Number(curve.Area),
                _ => null,
            };
        }

        return new
        {
            Hatch = appended,
            Pattern = hatch.IsGradient ? null : hatch.PatternName,
            Gradient = hatch.IsGradient ? hatch.GradientName : null,
            Associative = hatch.Associative,
            Area = area,
            AreaNote = area is null ? "Aire calculée par AutoCAD après l'appel : lisez-la avec list_entities." : null,
        };
    }

    private static readonly string[] GradientNames =
        ["LINEAR", "CYLINDER", "INVCYLINDER", "SPHERICAL", "INVSPHERICAL", "HEMISPHERICAL", "INVHEMISPHERICAL", "CURVED", "INVCURVED"];

    /// <summary>Couleurs du dégradé : une (vers le blanc) ou deux ; bleu vers blanc par défaut.</summary>
    private static GradientColor[] ReadGradientColors(ArgReader reader)
    {
        var colors = reader.GetStrings("gradientColors");
        if (colors.Count > 2)
            throw new PipeException(PipeErrorCodes.InvalidParams, "« gradientColors » : une ou deux couleurs.");

        var first = colors.Count > 0 ? EditTools.ParseColor(colors[0]) : EditTools.ParseColor("5");
        var second = colors.Count > 1 ? EditTools.ParseColor(colors[1]) : EditTools.ParseColor("255,255,255");
        return [new GradientColor(first, 0f), new GradientColor(second, 1f)];
    }

    /// <summary>
    /// Ligne de repère multiple (MLEADER) : pointe de flèche sur le premier point, éventuels points intermédiaires,
    /// puis le texte au bout du dernier point.
    /// </summary>
    public static object CreateLeader(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var points = reader.RequirePoints("points", minimum: 2);
        var text = reader.RequireString("text");

        var leader = new MLeader();
        leader.SetDatabaseDefaults();
        if (reader.Has("style"))
            leader.MLeaderStyle = LeaderStyle(context, reader.GetString("style")!);

        leader.ContentType = ContentType.MTextContent;
        var mtext = new MText();
        mtext.SetDatabaseDefaults();
        mtext.Contents = EscapeMText(text);
        if (reader.Has("height"))
            mtext.TextHeight = PositiveOrError(reader, "height");

        // AutoCAD place le texte du côté où il se trouve par rapport au dernier point : on le pose d'abord nettement
        // du côté vers lequel pointe le dernier segment, puis on corrige sa position exacte plus bas.
        var side = points[^1].X >= points[^2].X ? 1 : -1;
        var reach = 100 * Math.Max(1, mtext.TextHeight > 0 ? mtext.TextHeight : leader.TextHeight);
        mtext.Location = reader.GetPoint("textPosition", points[^1] + new Vector3d(side * reach, 0, 0));
        leader.MText = mtext;

        // La ligne est créée sur le dernier point (côté texte), puis les autres sont ajoutés devant jusqu'à la pointe.
        var index = leader.AddLeaderLine(points[^1]);
        for (var i = points.Count - 2; i >= 0; i--)
            leader.AddFirstVertex(index, points[i]);

        if (reader.Has("height"))
            leader.TextHeight = PositiveOrError(reader, "height");

        if (reader.Has("arrowSize"))
            leader.ArrowSize = PositiveOrError(reader, "arrowSize");

        var appended = EditTools.Append(context, leader, reader);

        // AutoCAD recalcule le dernier sommet d'après la position du texte et le palier : on translate le texte
        // de l'écart pour que la ligne aboutisse au point demandé (sauf position de texte imposée).
        if (!reader.Has("textPosition"))
        {
            var gap = points[^1] - leader.GetLastVertex(index);
            if (gap.Length > 1e-9)
                leader.TextLocation += gap;
        }

        var style = (MLeaderStyle)transaction.GetObject(leader.MLeaderStyle, OpenMode.ForRead);
        return new
        {
            Leader = appended,
            Arrow = Format.Point(leader.GetFirstVertex(index)),
            Landing = Format.Point(leader.GetLastVertex(index)),
            Text = leader.MText.Text,
            Style = style.Name,
            TextHeight = Format.Number(leader.TextHeight),
            ArrowSize = Format.Number(leader.ArrowSize),
        };
    }

    private static ObjectId LeaderStyle(ToolContext context, string name)
    {
        var transaction = context.RequireTransaction();
        var styles = (DBDictionary)transaction.GetObject(context.Database.MLeaderStyleDictionaryId, OpenMode.ForRead);
        if (styles.Contains(name))
            return styles.GetAt(name);

        throw new PipeException(PipeErrorCodes.InvalidParams,
            $"Le style de ligne de repère « {name} » n'existe pas. Styles du dessin : " +
            $"{string.Join(", ", styles.Cast<System.Collections.DictionaryEntry>().Select(entry => (string)entry.Key))}.");
    }

    private static double PositiveOrError(ArgReader reader, string name) => Positive(reader, name);

    /// <summary>
    /// Cotations : alignée, horizontale, verticale, pivotée, rayon, diamètre, angulaire, coordonnée (ordinate) et
    /// longueur d'arc. Le texte mesuré est celui d'AutoCAD, sauf texte imposé (« &lt;&gt; » y insère la mesure).
    /// </summary>
    public static object CreateDimension(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var type = reader.RequireString("type").Trim().ToLowerInvariant();
        var text = reader.GetString("text") ?? "";
        var styleId = reader.Has("style") ? DimensionStyle(context, reader.GetString("style")!) : database.Dimstyle;

        Dimension dimension = type switch
        {
            "aligned" or "horizontal" or "vertical" or "rotated" => LinearDimension(reader, type, text, styleId),
            "radius" or "diameter" => RadialDimension(context, reader, type, text, styleId, transaction),
            "angular" => AngularDimension(reader, text, styleId),
            "ordinate" => OrdinateDimension(reader, text, styleId),
            "arc_length" => ArcLengthDimension(context, reader, text, styleId, transaction),
            _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Type de cotation « {type} » inconnu : aligned, horizontal, vertical, rotated, radius, diameter, angular, " +
                "ordinate ou arc_length."),
        };

        var appended = EditTools.Append(context, dimension, reader);
        var style = (DimStyleTableRecord)transaction.GetObject(dimension.DimensionStyle, OpenMode.ForRead);
        return new
        {
            Dimension = appended,
            Type = type,
            Measurement = DimensionStyleTools.Measurement(dimension),
            Unit = type == "angular" ? "degrés" : "unités du dessin",
            Style = style.Name,

            // Hauteur réelle du texte de cote : à comparer à l'échelle du dessin (en mètres, 2.5 = 2,5 m).
            TextHeight = Format.Number(dimension.Dimtxt * (dimension.Dimscale > 0 ? dimension.Dimscale : 1)),
        };
    }

    private static Dimension LinearDimension(ArgReader reader, string type, string text, ObjectId styleId)
    {
        var start = reader.RequirePoint("start");
        var end = reader.RequirePoint("end");
        if (start.DistanceTo(end) < 1e-9)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Les points start et end sont confondus.");

        var rotation = type switch
        {
            "horizontal" => 0.0,
            "vertical" => Math.PI / 2,
            "rotated" => Format.Radians(reader.RequireDouble("angle")),
            _ => Math.Atan2(end.Y - start.Y, end.X - start.X),
        };

        var dimLinePoint = reader.Has("dimLinePoint")
            ? reader.RequirePoint("dimLinePoint")
            : OffsetPoint(start, end, rotation, reader.Has("offset")
                ? reader.RequireDouble("offset")
                : throw new PipeException(PipeErrorCodes.InvalidParams,
                    "Placez la ligne de cote avec « offset » (distance) ou « dimLinePoint » (point par lequel elle passe)."));

        return type == "aligned"
            ? new AlignedDimension(start, end, dimLinePoint, text, styleId)
            : new RotatedDimension(rotation, start, end, dimLinePoint, text, styleId);
    }

    /// <summary>Point de la ligne de cote, décalé perpendiculairement à sa direction (positif à gauche).</summary>
    private static Point3d OffsetPoint(Point3d start, Point3d end, double rotation, double offset)
    {
        var middle = new Point3d((start.X + end.X) / 2, (start.Y + end.Y) / 2, (start.Z + end.Z) / 2);
        return middle + new Vector3d(-Math.Sin(rotation), Math.Cos(rotation), 0) * offset;
    }

    private static Dimension RadialDimension(ToolContext context, ArgReader reader, string type, string text, ObjectId styleId, Transaction transaction)
    {
        Point3d center;
        double radius;
        if (reader.Has("handle"))
        {
            var id = Handles.Resolve(context, reader, "handle").Single();
            (center, radius) = transaction.GetObject(id, OpenMode.ForRead) switch
            {
                Circle circle => (circle.Center, circle.Radius),
                Arc arc => (arc.Center, arc.Radius),
                _ => throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est ni un cercle ni un arc."),
            };
        }
        else
        {
            center = reader.RequirePoint("center");
            radius = Positive(reader, "radius");
        }

        var angle = Format.Radians(reader.GetDouble("angle", 45));
        var direction = new Vector3d(Math.Cos(angle), Math.Sin(angle), 0);
        var chordPoint = center + direction * radius;
        return type == "radius"
            ? new RadialDimension(center, chordPoint, 0, text, styleId)
            : new DiametricDimension(chordPoint, center - direction * radius, 0, text, styleId);
    }

    private static Dimension AngularDimension(ArgReader reader, string text, ObjectId styleId)
    {
        var center = reader.RequirePoint("center");
        var start = reader.RequirePoint("start");
        var end = reader.RequirePoint("end");
        Point3d arcPoint;
        if (reader.Has("arcPoint"))
        {
            arcPoint = reader.RequirePoint("arcPoint");
        }
        else
        {
            // Arc de cote placé sur la bissectrice, au rayon demandé (par défaut, la plus courte des deux branches).
            var first = (start - center).GetNormal();
            var second = (end - center).GetNormal();
            var bisector = first + second;
            bisector = bisector.IsZeroLength() ? first.GetPerpendicularVector() : bisector.GetNormal();
            var radius = reader.GetDouble("radius", Math.Min(center.DistanceTo(start), center.DistanceTo(end)));
            arcPoint = center + bisector * radius;
        }

        return new Point3AngularDimension(center, start, end, arcPoint, text, styleId);
    }

    /// <summary>
    /// Cote de coordonnée : X ou Y du point start, mesuré depuis origin (0,0 par défaut), ligne de repère jusqu'à end.
    /// Sans axis, AutoCAD choisit comme la commande COTORD : ligne de repère plutôt verticale, abscisse X mesurée.
    /// </summary>
    private static Dimension OrdinateDimension(ArgReader reader, string text, ObjectId styleId)
    {
        var feature = reader.RequirePoint("start");
        var leaderEnd = reader.RequirePoint("end");
        if (feature.DistanceTo(leaderEnd) < 1e-9)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Les points start (point coté) et end (bout de la ligne de repère) sont confondus.");

        var axis = reader.GetString("axis")?.Trim().ToLowerInvariant()
            ?? (Math.Abs(leaderEnd.Y - feature.Y) >= Math.Abs(leaderEnd.X - feature.X) ? "x" : "y");
        if (axis is not ("x" or "y"))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"« axis » vaut x (abscisse mesurée) ou y (ordonnée), pas « {axis} ».");

        return new OrdinateDimension(axis == "x", feature, leaderEnd, text, styleId)
        {
            Origin = reader.GetPoint("origin", Point3d.Origin),
        };
    }

    /// <summary>Cote de longueur d'arc sur un arc existant, placée à offset du milieu de l'arc (vers l'extérieur).</summary>
    private static Dimension ArcLengthDimension(ToolContext context, ArgReader reader, string text, ObjectId styleId, Transaction transaction)
    {
        var id = Handles.Resolve(context, reader, "handle").Single();
        var arc = transaction.GetObject(id, OpenMode.ForRead) as Arc
            ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas un arc.");

        Point3d arcPoint;
        if (reader.Has("arcPoint"))
        {
            arcPoint = reader.RequirePoint("arcPoint");
        }
        else
        {
            var offset = reader.Has("offset") ? reader.RequireDouble("offset") : arc.Radius * 0.2;
            var middle = arc.GetPointAtDist(arc.Length / 2);
            arcPoint = middle + (middle - arc.Center).GetNormal() * offset;
        }

        return new ArcDimension(arc.Center, arc.StartPoint, arc.EndPoint, arcPoint, text, styleId);
    }

    /// <summary>Texte brut vers le format MTEXT : caractères de mise en forme protégés, retours à la ligne en \P.</summary>
    private static string EscapeMText(string text) =>
        text.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}")
            .Replace("\r\n", "\n").Replace("\n", "\\P");

    private static AttachmentPoint ParseAttachment(string value) => value.Trim().ToLowerInvariant() switch
    {
        "top_left" => AttachmentPoint.TopLeft,
        "top_center" => AttachmentPoint.TopCenter,
        "top_right" => AttachmentPoint.TopRight,
        "middle_left" => AttachmentPoint.MiddleLeft,
        "middle_center" => AttachmentPoint.MiddleCenter,
        "middle_right" => AttachmentPoint.MiddleRight,
        "bottom_left" => AttachmentPoint.BottomLeft,
        "bottom_center" => AttachmentPoint.BottomCenter,
        "bottom_right" => AttachmentPoint.BottomRight,
        _ => throw new PipeException(PipeErrorCodes.InvalidParams,
            $"Point d'attache « {value} » inconnu : top_left, top_center, top_right, middle_left, middle_center, " +
            "middle_right, bottom_left, bottom_center ou bottom_right."),
    };

    internal static ObjectId TextStyle(ToolContext context, string name)
    {
        var styles = (TextStyleTable)context.RequireTransaction().GetObject(context.Database.TextStyleTableId, OpenMode.ForRead);
        return styles.Has(name)
            ? styles[name]
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"Le style de texte « {name} » n'existe pas dans le dessin.");
    }

    private static ObjectId DimensionStyle(ToolContext context, string name)
    {
        var styles = (DimStyleTable)context.RequireTransaction().GetObject(context.Database.DimStyleTableId, OpenMode.ForRead);
        if (styles.Has(name))
            return styles[name];

        var names = new List<string>();
        foreach (var id in styles)
            names.Add(((DimStyleTableRecord)context.RequireTransaction().GetObject(id, OpenMode.ForRead)).Name);

        throw new PipeException(PipeErrorCodes.InvalidParams,
            $"Le style de cote « {name} » n'existe pas. Styles du dessin : {string.Join(", ", names)}.");
    }

    private static double Positive(ArgReader reader, string name)
    {
        var value = reader.RequireDouble(name);
        return value > 0
            ? value
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » doit être strictement positif.");
    }

    private static double? TryArea(Hatch hatch)
    {
        try
        {
            return Format.Number(hatch.Area);
        }
        catch (AcException)
        {
            return null;
        }
    }
}
