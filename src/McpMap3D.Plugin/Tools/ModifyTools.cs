using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>Modifications de dessin technique : copie, miroir, échelle, décalage, édition des textes.</summary>
internal static partial class ModifyTools
{
    private const int MaxCopies = 1000;

    /// <summary>
    /// Copie des objets d'un vecteur ; avec count &gt; 1, copies successives (réseau linéaire).
    /// La copie est profonde : attributs de blocs et données d'objet suivent, comme avec la commande COPIER.
    /// </summary>
    public static object CopyEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var displacement = ReadDisplacement(reader);
        var count = reader.GetInt("count", 1, min: 1, max: MaxCopies);
        var sources = Handles.Resolve(context, reader);
        if (sources.Count * count > 20000)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Trop de copies en une fois (20 000 objets au maximum).");

        var copies = new List<string[]>();
        for (var step = 1; step <= count; step++)
        {
            var matrix = Matrix3d.Displacement(displacement * step);
            var handles = new List<string>();
            foreach (var (_, clone) in Cloning.DeepClone(context, sources))
            {
                var entity = (Entity)transaction.GetObject(clone, OpenMode.ForWrite);
                entity.TransformBy(matrix);
                handles.Add(entity.Handle.ToString());
            }

            copies.Add([.. handles]);
        }

        return new
        {
            Copies = count,
            Created = copies.Sum(handles => handles.Length),
            Displacement = Format.Vector(displacement),
            Handles = count == 1 ? (object)copies[0] : copies,
        };
    }

    /// <summary>
    /// Symétrie par rapport à un axe du plan XY. Par défaut les originaux sont conservés, et les textes restent
    /// lisibles (comportement de la commande MIROIR avec MIRRTEXT = 0).
    /// </summary>
    public static object MirrorEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var axisStart = reader.RequirePoint("axisStart");
        var axisEnd = reader.RequirePoint("axisEnd");
        if (new Point2d(axisStart.X, axisStart.Y).GetDistanceTo(new Point2d(axisEnd.X, axisEnd.Y)) < 1e-9)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Les deux points de l'axe de symétrie sont confondus en plan.");

        var eraseOriginals = reader.GetBool("eraseOriginals", false);
        var keepTextReadable = reader.GetBool("keepTextReadable", true);

        // Axe vertical à travers le plan XY : un plan miroir, pour ne pas inverser les Z.
        var axis = new Vector3d(axisEnd.X - axisStart.X, axisEnd.Y - axisStart.Y, 0).GetNormal();
        var mirror = Matrix3d.Mirroring(new Plane(axisStart, axis.CrossProduct(Vector3d.ZAxis)));
        return Mirror(context, reader, mirror, eraseOriginals, keepTextReadable);
    }

    /// <summary>Symétrie par rapport à un plan quelconque de l'espace (commande 3DMIROIR).</summary>
    public static object MirrorEntities3d(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var plane = ModelingTools.ReadPlane(reader);
        return Mirror(context, reader, Matrix3d.Mirroring(plane),
            reader.GetBool("eraseOriginals", false), reader.GetBool("keepTextReadable", true));
    }

    private static object Mirror(ToolContext context, ArgReader reader, Matrix3d mirror, bool eraseOriginals, bool keepTextReadable)
    {
        var transaction = context.RequireTransaction();
        var sources = Handles.Resolve(context, reader);
        List<ObjectId> targets;
        if (eraseOriginals)
        {
            targets = [.. sources];
        }
        else
        {
            targets = Cloning.DeepClone(context, sources).Select(pair => pair.Clone).ToList();
        }

        var handles = new List<string>();
        foreach (var id in targets)
        {
            var entity = eraseOriginals ? EditTools.OpenForWrite(id, transaction, context) : (Entity)transaction.GetObject(id, OpenMode.ForWrite);
            entity.TransformBy(mirror);
            if (keepTextReadable)
                MakeReadable(entity, transaction);

            handles.Add(entity.Handle.ToString());
        }

        return new { Mirrored = handles.Count, OriginalsKept = !eraseOriginals, Handles = handles };
    }

    public static object ScaleEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var basePoint = reader.RequirePoint("basePoint");
        var factor = reader.RequireDouble("factor");
        if (factor <= 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le facteur d'échelle doit être strictement positif.");

        var matrix = Matrix3d.Scaling(factor, basePoint);
        var scaled = new List<string>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            var entity = EditTools.OpenForWrite(id, transaction, context);
            entity.TransformBy(matrix);
            scaled.Add(entity.Handle.ToString());
        }

        return new { Scaled = scaled.Count, Factor = factor, Handles = scaled };
    }

    /// <summary>
    /// Décalage (parallèle) de courbes. Le côté se donne par un point (« side »), comme avec la commande DECALER ;
    /// sans point, le signe de la distance fixe le côté selon le sens de la courbe.
    /// </summary>
    public static object OffsetEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var distance = reader.RequireDouble("distance");
        if (distance == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "La distance de décalage ne peut pas être nulle.");

        Point3d? side = reader.Has("side") ? reader.RequirePoint("side") : null;
        var created = new List<string>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not Curve curve)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas une courbe : il ne peut pas être décalé.");

            var offsets = side is Point3d point
                ? ClosestSide(curve, Math.Abs(distance), point)
                : Offset(curve, distance);

            foreach (Entity offset in offsets)
            {
                var space = (BlockTableRecord)transaction.GetObject(curve.OwnerId, OpenMode.ForWrite);
                space.AppendEntity(offset);
                transaction.AddNewlyCreatedDBObject(offset, true);
                created.Add(offset.Handle.ToString());
            }
        }

        return new { Created = created.Count, Handles = created };
    }

    /// <summary>
    /// Décompose des objets d'un niveau (bloc, polyligne, cote, hachure, texte multiligne…). Pour une référence de
    /// bloc, les attributs deviennent par défaut des textes portant leur valeur, comme ÉCLATEBLOC, plutôt que
    /// des définitions d'attributs affichant leur étiquette.
    /// </summary>
    public static object ExplodeEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var attributesAsText = reader.GetBool("attributesAsText", true);

        var exploded = new List<object>();
        var refused = new List<string>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            var entity = EditTools.OpenForWrite(id, transaction, context);
            var pieces = new DBObjectCollection();
            try
            {
                entity.Explode(pieces);
            }
            catch (AcException)
            {
                refused.Add($"{entity.Handle} ({Format.DxfName(entity)})");
                continue;
            }

            if (pieces.Count == 0)
            {
                refused.Add($"{entity.Handle} ({Format.DxfName(entity)})");
                continue;
            }

            var results = pieces.Cast<Entity>().ToList();
            if (entity is BlockReference reference && attributesAsText)
            {
                foreach (var definition in results.OfType<AttributeDefinition>().Where(d => !d.Constant).ToList())
                {
                    results.Remove(definition);
                    definition.Dispose();
                }

                results.AddRange(AttributesAsText(reference, transaction));
            }

            var space = (BlockTableRecord)transaction.GetObject(entity.OwnerId, OpenMode.ForWrite);
            var created = new List<string>();
            foreach (var piece in results)
            {
                space.AppendEntity(piece);
                transaction.AddNewlyCreatedDBObject(piece, true);
                created.Add(piece.Handle.ToString());
            }

            var type = Format.DxfName(entity);
            entity.Erase();
            exploded.Add(new { Handle = id.Handle.ToString(), Type = type, Created = created.Count, Handles = created });
        }

        if (exploded.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Aucun objet décomposable : {string.Join(", ", refused)} (lignes, cercles, arcs et textes simples ne se décomposent pas).");

        return new { Exploded = exploded.Count, Results = exploded, NotExplodable = refused.Count > 0 ? refused : null };
    }

    /// <summary>Textes équivalents aux attributs visibles d'une référence de bloc (valeur, position, style).</summary>
    private static IEnumerable<Entity> AttributesAsText(BlockReference reference, Transaction transaction)
    {
        foreach (ObjectId id in reference.AttributeCollection)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not AttributeReference attribute || attribute.Invisible)
                continue;

            if (attribute.IsMTextAttribute)
            {
                var mtext = (MText)attribute.MTextAttribute.Clone();
                mtext.SetPropertiesFrom(attribute);
                yield return mtext;
                continue;
            }

            var text = new DBText();
            text.SetPropertiesFrom(attribute);
            text.TextStyleId = attribute.TextStyleId;
            text.TextString = attribute.TextString;
            text.Height = attribute.Height;
            text.WidthFactor = attribute.WidthFactor;
            text.Oblique = attribute.Oblique;
            text.Rotation = attribute.Rotation;
            text.Normal = attribute.Normal;
            text.Position = attribute.Position;
            text.HorizontalMode = attribute.HorizontalMode;
            text.VerticalMode = attribute.VerticalMode;
            if (attribute.HorizontalMode != TextHorizontalMode.TextLeft || attribute.VerticalMode != TextVerticalMode.TextBase)
                text.AlignmentPoint = attribute.AlignmentPoint;

            yield return text;
        }
    }

    /// <summary>
    /// Modifie un texte (TEXT, MTEXT), le texte imposé d'une cote, les cellules d'un tableau ou les attributs d'une
    /// référence de bloc.
    /// </summary>
    public static object EditText(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var id = Handles.Resolve(context, reader, "handle").Single();
        var entity = EditTools.OpenForWrite(id, transaction, context);
        var height = reader.Has("height") ? reader.RequireDouble("height") : (double?)null;
        if (height <= 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "La hauteur du texte doit être supérieure à zéro.");

        switch (entity)
        {
            case DBText text:
                if (reader.Has("text"))
                    text.TextString = reader.GetString("text")!;
                if (height is double textHeight)
                    text.Height = textHeight;
                return new { Handle = text.Handle.ToString(), Type = "TEXT", Text = text.TextString, Height = Format.Number(text.Height) };

            case MText mtext:
                if (reader.Has("text"))
                    mtext.Contents = reader.GetString("text")!.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}")
                        .Replace("\r\n", "\n").Replace("\n", "\\P");
                if (height is double mtextHeight)
                    mtext.TextHeight = mtextHeight;
                return new { Handle = mtext.Handle.ToString(), Type = "MTEXT", Text = mtext.Text, Height = Format.Number(mtext.TextHeight) };

            case MLeader leader when leader.MText is not null:
                // Le texte d'une ligne de repère se lit et se remplace en bloc : on modifie une copie, puis on la réaffecte.
                var content = leader.MText;
                if (reader.Has("text"))
                    content.Contents = reader.GetString("text")!.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}")
                        .Replace("\r\n", "\n").Replace("\n", "\\P");
                if (height is double leaderHeight)
                    content.TextHeight = leaderHeight;
                leader.MText = content;
                if (height is double styleHeight)
                    leader.TextHeight = styleHeight;
                return new { Handle = leader.Handle.ToString(), Type = "MULTILEADER", Text = leader.MText.Text };

            case Dimension dimension:
                // Chaîne vide : retour à la valeur mesurée ; « <> » insère la mesure dans le texte imposé.
                if (reader.Has("text"))
                    dimension.DimensionText = reader.GetString("text")!;
                return new { Handle = dimension.Handle.ToString(), Type = "DIMENSION", Text = dimension.DimensionText };

            case Table table:
                return DraftingTools.EditTableCells(table, reader, height);

            case BlockReference reference:
                var values = BlockTools.ReadAttributeValues(reader);
                if (values.Count == 0)
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        "Pour une référence de bloc, indiquez « attributes », par exemple {\"TITRE\": \"Plan de masse\"}.");

                var updated = new List<string>();
                foreach (ObjectId attributeId in reference.AttributeCollection)
                {
                    var attribute = (AttributeReference)transaction.GetObject(attributeId, OpenMode.ForWrite);
                    if (!values.TryGetValue(attribute.Tag, out var value))
                        continue;

                    attribute.TextString = value;
                    if (height is double attributeHeight)
                        attribute.Height = attributeHeight;
                    updated.Add(attribute.Tag);
                }

                var missing = values.Keys.Where(tag => !updated.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missing.Count > 0)
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"Attribut(s) absent(s) de cette référence : {string.Join(", ", missing)}. " +
                        $"Présents : {string.Join(", ", BlockTools.AttributeValues(reference, transaction).Keys)}.");

                return new { Handle = reference.Handle.ToString(), Type = "INSERT", Attributes = BlockTools.AttributeValues(reference, transaction) };

            default:
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"L'objet {id.Handle} ({Format.DxfName(entity)}) n'est ni un texte, ni une cote, ni une ligne de repère, ni un tableau, " +
                    "ni une référence de bloc.");
        }
    }

    private static Vector3d ReadDisplacement(ArgReader reader)
    {
        if (reader.Has("displacement"))
        {
            var vector = reader.RequirePoint("displacement");
            return new Vector3d(vector.X, vector.Y, vector.Z);
        }

        if (reader.Has("from") && reader.Has("to"))
            return reader.RequirePoint("to") - reader.RequirePoint("from");

        throw new PipeException(PipeErrorCodes.InvalidParams,
            "Indiquez le déplacement : soit « displacement » ([dx, dy] ou [dx, dy, dz]), soit « from » et « to ».");
    }

    /// <summary>Après une symétrie, remet les textes à l'endroit, comme MIROIR avec MIRRTEXT = 0.</summary>
    private static void MakeReadable(Entity entity, Transaction transaction)
    {
        switch (entity)
        {
            case DBText text when text.IsMirroredInX || text.IsMirroredInY:
                text.IsMirroredInX = false;
                text.IsMirroredInY = false;
                break;

            case MText mtext when mtext.Normal.Z < 0:
                // Le texte multiligne, retourné face arrière, est remis dans le plan XY dans le même sens de lecture.
                var direction = mtext.Direction;
                mtext.Normal = Vector3d.ZAxis;
                mtext.Direction = new Vector3d(-direction.X, -direction.Y, 0).GetNormal();
                break;

            case BlockReference reference:
                foreach (ObjectId id in reference.AttributeCollection)
                {
                    if (transaction.GetObject(id, OpenMode.ForWrite) is AttributeReference attribute
                        && (attribute.IsMirroredInX || attribute.IsMirroredInY))
                    {
                        attribute.IsMirroredInX = false;
                        attribute.IsMirroredInY = false;
                    }
                }

                break;
        }
    }

    private static DBObjectCollection Offset(Curve curve, double distance)
    {
        try
        {
            return curve.GetOffsetCurves(distance);
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Décalage impossible de {curve.Handle} à {distance} ({ex.ErrorStatus}) : distance trop grande pour la courbe ?");
        }
    }

    /// <summary>Des deux décalages possibles, garde celui qui passe du côté du point.</summary>
    private static DBObjectCollection ClosestSide(Curve curve, double distance, Point3d side)
    {
        DBObjectCollection? best = null;
        var bestDistance = double.MaxValue;
        foreach (var signed in new[] { distance, -distance })
        {
            DBObjectCollection candidates;
            try
            {
                candidates = curve.GetOffsetCurves(signed);
            }
            catch (AcException)
            {
                continue; // Côté impossible, par exemple un cercle plus petit que la distance vers l'intérieur.
            }

            var nearest = candidates.Cast<Curve>().Select(c => c.GetClosestPointTo(side, false).DistanceTo(side)).DefaultIfEmpty(double.MaxValue).Min();
            if (nearest < bestDistance)
            {
                foreach (DBObject previous in best ?? [])
                    previous.Dispose();

                best = candidates;
                bestDistance = nearest;
            }
            else
            {
                foreach (DBObject rejected in candidates)
                    rejected.Dispose();
            }
        }

        return best ?? throw new PipeException(PipeErrorCodes.InvalidParams,
            $"Décalage impossible de {curve.Handle} à {distance} des deux côtés.");
    }
}
