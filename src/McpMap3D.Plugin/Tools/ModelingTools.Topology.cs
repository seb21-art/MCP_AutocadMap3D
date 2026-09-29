using System.Text.Json;
using Autodesk.AutoCAD.BoundaryRepresentation;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;
using BrepEdge = Autodesk.AutoCAD.BoundaryRepresentation.Edge;
using BrepFace = Autodesk.AutoCAD.BoundaryRepresentation.Face;
using DbSurface = Autodesk.AutoCAD.DatabaseServices.Surface;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Édition fine des solides : lecture des faces et arêtes (B-rep), congés, chanfreins, coque, édition de faces,
/// épaississement de surfaces et séparation des corps disjoints. Les faces et arêtes se désignent par leur indice
/// (renvoyé par get_solid_topology) ou par un filtre géométrique.
/// </summary>
internal static partial class ModelingTools
{
    private const int MaxListedFaces = 200;
    private const int MaxListedEdges = 400;
    private const double Tolerance = 1e-6;

    public static object GetSolidTopology(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var id = SingleHandle(context, reader);
        if (transaction.GetObject(id, OpenMode.ForRead) is not Solid3d solid)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas un solide 3D.");

        var topology = ReadTopology(solid);
        return new
        {
            Handle = id.Handle.ToString(),
            FaceCount = topology.Faces.Count,
            EdgeCount = topology.Edges.Count,
            Faces = topology.Faces.Take(MaxListedFaces).Select(face => new
            {
                face.Index,
                face.Type,
                Area = face.Area is double area ? Format.Number(area) : (double?)null,
                Normal = face.Normal is Vector3d normal ? Format.Vector(normal) : null,
                Center = face.Box is Extents3d box ? Format.Point(Center(box)) : null,
                face.Edges,
            }).ToArray(),
            Edges = topology.Edges.Take(MaxListedEdges).Select(edge => new
            {
                edge.Index,
                edge.Kind,
                Length = edge.Length is double length ? Format.Number(length) : (double?)null,
                Start = edge.Start is Point3d start ? Format.Point(start) : null,
                End = edge.End is Point3d end ? Format.Point(end) : null,
                Radius = edge.Radius is double radius ? Format.Number(radius) : (double?)null,
                Orientation = edge.IsVertical ? "vertical" : edge.IsHorizontal ? "horizontal" : null,
                edge.Faces,
            }).ToArray(),
            Truncated = topology.Faces.Count > MaxListedFaces || topology.Edges.Count > MaxListedEdges ? true : (bool?)null,
        };
    }

    /// <summary>Face désignée par l'indice de get_solid_topology. Le solide ne doit pas avoir été modifié depuis.</summary>
    internal static SubentityId RequireFace(Solid3d solid, int index)
    {
        var topology = ReadTopology(solid);
        return (uint)index < (uint)topology.Faces.Count
            ? topology.Faces[index].Id
            : throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Face {index} hors du solide {solid.Handle} : indices 0 à {topology.Faces.Count - 1} (voir get_solid_topology).");
    }

    public static object FilletEdges(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var radius = Positive(reader, "radius");
        var (solid, topology) = OpenSolidWithTopology(context, reader);
        var edges = SelectEdges(topology, reader);

        var radii = new DoubleCollection();
        var setbacks = new DoubleCollection();
        foreach (var _ in edges)
        {
            radii.Add(radius);
            setbacks.Add(0);
        }

        Modify(solid, "raccorder les arêtes", "Le rayon est peut-être trop grand pour les faces voisines.",
            () => solid.FilletEdges([.. edges.Select(edge => edge.Id)], radii, setbacks, setbacks));
        return new { Handle = solid.Handle.ToString(), Edges = edges.Count, Radius = radius, Volume = TryVolume(solid) };
    }

    /// <summary>Chanfreins des arêtes d'une face de base, comme la commande CHANFREINARETE.</summary>
    public static object ChamferEdges(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var distance = Positive(reader, "distance");
        var otherDistance = reader.Has("otherDistance") ? Positive(reader, "otherDistance") : distance;
        var (solid, topology) = OpenSolidWithTopology(context, reader);

        var baseFaces = SelectFaces(topology, reader, "baseFace", "baseFaceFilter");
        if (baseFaces.Count != 1)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Le chanfrein demande une seule face de base, et la sélection en désigne {baseFaces.Count} : " +
                "indiquez « baseFace » par son indice (voir get_solid_topology).");

        var baseFace = baseFaces[0];
        var edges = reader.Has("edges") ? SelectEdges(topology, reader) : baseFace.Edges.Select(index => topology.Edges[index]).ToList();
        var foreign = edges.Where(edge => !baseFace.Edges.Contains(edge.Index)).Select(edge => edge.Index).ToList();
        if (foreign.Count > 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Les arêtes {string.Join(", ", foreign)} ne bordent pas la face de base {baseFace.Index}.");

        Modify(solid, "chanfreiner les arêtes", "Les distances sont peut-être trop grandes pour les faces voisines.",
            () => solid.ChamferEdges([.. edges.Select(edge => edge.Id)], baseFace.Id, distance, otherDistance));
        return new
        {
            Handle = solid.Handle.ToString(),
            BaseFace = baseFace.Index,
            Edges = edges.Count,
            Distance = distance,
            OtherDistance = otherDistance,
            Volume = TryVolume(solid),
        };
    }

    /// <summary>Coque d'épaisseur constante, avec des faces ouvertes en option (commande SOLIDEDIT, Gainer).</summary>
    public static object ShellSolid(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var thickness = Positive(reader, "thickness");
        var outward = reader.GetBool("outward", false);
        var (solid, topology) = OpenSolidWithTopology(context, reader);
        var removed = reader.Has("removeFaces") || reader.Has("removeFaceFilter")
            ? SelectFaces(topology, reader, "removeFaces", "removeFaceFilter")
            : new List<FaceInfo>();
        if (removed.Count == topology.Faces.Count)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Il faut garder au moins une face : toutes les faces seraient ouvertes.");

        // Pour ShellBody, une distance positive décale les faces vers l'extérieur, une négative vers l'intérieur.
        var offset = outward ? thickness : -thickness;
        Modify(solid, "créer la coque", "L'épaisseur est peut-être trop grande pour la forme du solide.", () =>
        {
            if (removed.Count > 0)
                solid.ShellBody([.. removed.Select(face => face.Id)], offset);
            else
                ShellClosed(solid, [.. topology.Faces.Select(face => face.Id)], offset);
        });
        return new
        {
            Handle = solid.Handle.ToString(),
            Thickness = thickness,
            Outward = outward,
            OpenFaces = removed.Select(face => face.Index).ToArray(),
            Volume = TryVolume(solid),
        };
    }

    /// <summary>
    /// Coque fermée (aucune face ouverte) : ShellBody refuse un tableau de faces vide (IndexOutOfRangeException),
    /// la coque est donc le solide décalé de toutes ses faces, moins sa forme d'origine ou plus petite.
    /// Seuls les identifiants de faces du solide lui-même sont utilisés, ceux d'une copie pouvant différer.
    /// </summary>
    private static void ShellClosed(Solid3d solid, SubentityId[] faces, double offset)
    {
        if (offset > 0)
        {
            // Vers l'extérieur : le solide grossit, puis on retire sa forme d'origine.
            using var original = (Solid3d)solid.Clone();
            solid.OffsetFaces(faces, offset);
            solid.BooleanOperation(BooleanOperationType.BoolSubtract, original);
        }
        else
        {
            // Vers l'intérieur : le solide rétrécit, on lui rend sa forme extérieure puis on retire le noyau.
            using var outer = (Solid3d)solid.Clone();
            solid.OffsetFaces(faces, offset);
            using var inner = (Solid3d)solid.Clone();
            solid.BooleanOperation(BooleanOperationType.BoolUnite, outer);
            solid.BooleanOperation(BooleanOperationType.BoolSubtract, inner);
        }
    }

    /// <summary>
    /// Pousser, tirer, décaler, déplacer, faire pivoter, effiler, supprimer, colorer ou copier des faces d'un solide
    /// (commande SOLIDEDIT, Face).
    /// </summary>
    public static object EditSolidFaces(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var operation = reader.RequireString("operation").ToLowerInvariant();
        if (operation is not ("extrude" or "offset" or "move" or "rotate" or "taper" or "delete" or "color" or "copy"))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Opération « {operation} » inconnue : extrude, offset, move, rotate, taper, delete, color ou copy.");

        var distance = operation is "extrude" or "offset" ? reader.RequireDouble("distance") : 0;
        if (operation is "extrude" or "offset" && distance == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "La distance ne peut pas être nulle.");

        var displacement = Vector3d.XAxis;
        if (operation == "move")
        {
            var vector = reader.RequirePoint("displacement");
            displacement = new Vector3d(vector.X, vector.Y, vector.Z);
            if (displacement.IsZeroLength())
                throw new PipeException(PipeErrorCodes.InvalidParams, "Le déplacement ne peut pas être nul.");
        }

        var taperAngle = reader.GetDouble("taperAngle", 0);
        if (taperAngle != 0 && operation is not ("extrude" or "taper"))
            throw new PipeException(PipeErrorCodes.InvalidParams, "« taperAngle » ne s'applique qu'aux opérations extrude et taper.");

        if (operation == "taper" && taperAngle == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "L'opération taper demande un angle « taperAngle » non nul.");

        if (Math.Abs(taperAngle) >= 90)
            throw new PipeException(PipeErrorCodes.InvalidParams, "L'angle de dépouille doit être compris entre -90 et 90 degrés.");

        var taper = Format.Radians(taperAngle);
        var rotation = Matrix3d.Identity;
        if (operation == "rotate")
        {
            var angle = reader.RequireDouble("angle");
            var axis = reader.Has("axis") ? Direction(reader, "axis") : Vector3d.ZAxis;
            rotation = Matrix3d.Rotation(Format.Radians(angle), axis, reader.RequirePoint("basePoint"));
        }

        var draftDirection = operation == "taper" && reader.Has("draftDirection") ? Direction(reader, "draftDirection") : Vector3d.ZAxis;
        var faceColor = operation == "color" ? EditTools.ParseColor(reader.RequireString("color")) : null;

        var (solid, topology) = OpenSolidWithTopology(context, reader);
        var faces = SelectFaces(topology, reader, "faces", "faceFilter");
        if (operation == "copy")
            return CopyFaces(context, reader, solid, faces);

        // Charnière de la dépouille : par défaut le plan perpendiculaire à la direction passant par le point le plus bas
        // des faces, qui gardent ainsi leur arête basse.
        var basePoint = operation == "taper"
            ? reader.Has("basePoint") ? reader.RequirePoint("basePoint") : LowestPoint(faces, draftDirection)
            : Point3d.Origin;

        SubentityId[] ids = [.. faces.Select(face => face.Id)];
        Modify(solid, "modifier les faces", "La modification rendrait le solide invalide (faces qui se recoupent ?).", () =>
        {
            switch (operation)
            {
                case "extrude":
                    solid.ExtrudeFaces(ids, distance, taper);
                    break;
                case "offset":
                    solid.OffsetFaces(ids, distance);
                    break;
                case "move":
                    solid.TransformFaces(ids, Matrix3d.Displacement(displacement));
                    break;
                case "rotate":
                    solid.TransformFaces(ids, rotation);
                    break;
                case "taper":
                    solid.TaperFaces(ids, basePoint, draftDirection, taper);
                    break;
                case "color":
                    foreach (var id in ids)
                        solid.SetSubentityColor(id, faceColor!);

                    break;
                default:
                    solid.RemoveFaces(ids);
                    break;
            }
        });

        return new
        {
            Handle = solid.Handle.ToString(),
            Operation = operation,
            Faces = faces.Select(face => face.Index).ToArray(),
            BasePoint = operation == "taper" ? Format.Point(basePoint) : null,
            Volume = TryVolume(solid),
        };
    }

    /// <summary>Copies de faces sous forme de régions (faces planes) ou de corps, le solide restant intact.</summary>
    private static object CopyFaces(ToolContext context, ArgReader reader, Solid3d solid, List<FaceInfo> faces)
    {
        var copies = new List<object>();
        foreach (var face in faces)
        {
            Entity copy;
            try
            {
                copy = solid.CopyFace(face.Id);
            }
            catch (AcException ex)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"AutoCAD n'a pas pu copier la face {face.Index} du solide {solid.Handle} ({ex.ErrorStatus}).");
            }

            copy.SetPropertiesFrom(solid);
            var appended = EditTools.Append(context, copy, reader);
            copies.Add(new { Face = face.Index, Copy = appended, Area = copy is Region region ? TryNumber(() => region.Area) : null });
        }

        return new { Handle = solid.Handle.ToString(), Operation = "copy", Count = copies.Count, Copies = copies };
    }

    /// <summary>
    /// Imprime des courbes, régions, corps ou solides sur les faces d'un solide, comme la commande IMPRIMER : les faces
    /// touchées sont découpées suivant l'intersection, et les nouvelles faces se modifient ensuite une à une.
    /// </summary>
    public static object ImprintSolid(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var id = SingleHandle(context, reader);
        var sourceIds = Handles.Resolve(context, reader, "sources");
        if (sourceIds.Contains(id))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le solide ne peut pas figurer parmi les objets à imprimer.");

        var solid = OpenSolidForWrite(id, transaction, context);
        var before = TryReadTopology(solid);
        foreach (var sourceId in sourceIds)
        {
            var source = transaction.GetObject(sourceId, OpenMode.ForRead) as Entity
                ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {sourceId.Handle} n'est pas un objet graphique.");
            Modify(solid, $"imprimer l'objet {sourceId.Handle} sur les faces", "L'objet doit toucher ou traverser le solide.",
                () => solid.ImprintEntity(source));
        }

        var eraseSources = reader.GetBool("eraseSources", false);
        if (eraseSources)
        {
            foreach (var sourceId in sourceIds)
                EditTools.OpenForWrite(sourceId, transaction, context).Erase();
        }

        var after = TryReadTopology(solid);
        return new
        {
            Handle = solid.Handle.ToString(),
            Imprinted = sourceIds.Count,
            FacesBefore = before?.Faces.Count,
            FaceCount = after?.Faces.Count,
            EdgeCount = after?.Edges.Count,
            SourcesErased = eraseSources,
        };
    }

    /// <summary>
    /// Nettoie des solides, comme SOLIDEDIT Corps Nettoyer : retire les arêtes et sommets superflus, ceux qui séparent
    /// deux morceaux d'une même surface (après une union, par exemple).
    /// </summary>
    public static object CleanSolid(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();

        var results = new List<object>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            var solid = OpenSolidForWrite(id, transaction, context);
            var before = TryReadTopology(solid);
            Modify(solid, "nettoyer les arêtes", "", solid.CleanBody);
            var after = TryReadTopology(solid);
            results.Add(new
            {
                Handle = solid.Handle.ToString(),
                FacesBefore = before?.Faces.Count,
                Faces = after?.Faces.Count,
                EdgesBefore = before?.Edges.Count,
                Edges = after?.Edges.Count,
                Volume = TryVolume(solid),
            });
        }

        return new { Count = results.Count, Solids = results };
    }

    /// <summary>Solides obtenus en donnant une épaisseur à des surfaces ou à des régions.</summary>
    public static object ThickenSurface(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var thickness = reader.RequireDouble("thickness");
        if (thickness == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "L'épaisseur ne peut pas être nulle.");

        var bothSides = reader.GetBool("bothSides", false);
        var eraseSurfaces = reader.GetBool("eraseSurfaces", false);

        var solids = new List<object>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
            Solid3d solid;
            try
            {
                solid = entity switch
                {
                    DbSurface surface => surface.Thicken(thickness, bothSides),
                    Region region => ThickenRegion(region, thickness, bothSides),
                    _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"L'objet {id.Handle} ({Format.DxfName(entity)}) n'est ni une surface ni une région."),
                };
            }
            catch (AcException ex)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"AutoCAD n'a pas pu épaissir {id.Handle} ({ex.ErrorStatus}) : épaisseur trop grande pour sa courbure ?");
            }

            solid.SetPropertiesFrom(entity);
            solids.Add(Created(context, solid, reader));
            if (eraseSurfaces)
                EditTools.OpenForWrite(id, transaction, context).Erase();
        }

        return new { Count = solids.Count, Solids = solids, SurfacesErased = eraseSurfaces };
    }

    /// <summary>
    /// Sépare chaque solide en ses parties disjointes (après une soustraction qui l'a coupé en deux, par exemple),
    /// et nettoie au passage les arêtes et faces superflues.
    /// </summary>
    public static object SeparateSolid(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var clean = reader.GetBool("clean", true);

        var results = new List<object>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            var solid = OpenSolidForWrite(id, transaction, context);
            if (clean)
                Modify(solid, "nettoyer le solide", "", solid.CleanBody);

            var parts = Array.Empty<Solid3d>();
            Modify(solid, "séparer le solide", "", () => parts = solid.SeparateBody() ?? Array.Empty<Solid3d>());

            // Le solide garde la première partie ; les autres deviennent de nouveaux solides, dans le même espace.
            var space = (BlockTableRecord)transaction.GetObject(solid.OwnerId, OpenMode.ForWrite);
            var created = new List<object>();
            foreach (var part in parts)
            {
                part.SetPropertiesFrom(solid);
                space.AppendEntity(part);
                transaction.AddNewlyCreatedDBObject(part, true);
                created.Add(new { Handle = part.Handle.ToString(), Volume = TryVolume(part) });
            }

            results.Add(new { Handle = solid.Handle.ToString(), Volume = TryVolume(solid), NewParts = created });
        }

        return new { Count = results.Count, Solids = results, Cleaned = clean };
    }

    private static Solid3d ThickenRegion(Region region, double thickness, bool bothSides)
    {
        using var surface = new PlaneSurface();
        surface.CreateFromRegion(region);
        return surface.Thicken(thickness, bothSides);
    }

    private static (Solid3d Solid, SolidTopology Topology) OpenSolidWithTopology(ToolContext context, ArgReader reader)
    {
        var solid = OpenSolidForWrite(SingleHandle(context, reader), context.RequireTransaction(), context);
        return (solid, ReadTopology(solid));
    }

    /// <summary>Topologie lue pour un compte rendu : son échec ne doit pas faire échouer l'outil.</summary>
    private static SolidTopology? TryReadTopology(Solid3d solid)
    {
        try
        {
            return ReadTopology(solid);
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Point du plan perpendiculaire à <paramref name="direction"/> qui passe par le point le plus bas des faces,
    /// à l'aplomb du centre de leur boîte englobante.
    /// </summary>
    private static Point3d LowestPoint(IEnumerable<FaceInfo> faces, Vector3d direction)
    {
        var corners = faces.Where(face => face.Box is not null).SelectMany(face => Corners(face.Box!.Value)).ToList();
        if (corners.Count == 0)
            return Point3d.Origin;

        var lowest = corners.Min(corner => corner.GetAsVector().DotProduct(direction));
        var center = new Point3d(corners.Average(corner => corner.X), corners.Average(corner => corner.Y), corners.Average(corner => corner.Z));
        return center + direction * (lowest - center.GetAsVector().DotProduct(direction));
    }

    private static IEnumerable<Point3d> Corners(Extents3d box)
    {
        foreach (var x in new[] { box.MinPoint.X, box.MaxPoint.X })
        {
            foreach (var y in new[] { box.MinPoint.Y, box.MaxPoint.Y })
            {
                foreach (var z in new[] { box.MinPoint.Z, box.MaxPoint.Z })
                    yield return new Point3d(x, y, z);
            }
        }
    }

    /// <summary>Handle unique du solide à lire ou modifier : les indices de faces et d'arêtes lui sont propres.</summary>
    private static ObjectId SingleHandle(ToolContext context, ArgReader reader)
    {
        var ids = Handles.Resolve(context, reader, "handle");
        return ids.Count == 1
            ? ids[0]
            : throw new PipeException(PipeErrorCodes.InvalidParams,
                "Indiquez un seul solide dans « handle » : les indices de faces et d'arêtes sont propres à chaque solide.");
    }

    /// <summary>Exécute une modification d'AutoCAD sur un solide et traduit son échec en erreur lisible.</summary>
    private static void Modify(Solid3d solid, string action, string hint, Action modification)
    {
        try
        {
            modification();
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"AutoCAD n'a pas pu {action} du solide {solid.Handle} ({ex.ErrorStatus}). {hint}".TrimEnd());
        }
    }

    /// <summary>Faces désignées par indices (paramètre <paramref name="indicesName"/>) ou par un filtre géométrique.</summary>
    private static List<FaceInfo> SelectFaces(SolidTopology topology, ArgReader reader, string indicesName, string filterName)
    {
        if (reader.Has(indicesName))
            return ReadIndices(reader, indicesName, topology.Faces.Count).Select(index => topology.Faces[index]).ToList();

        var filter = reader.GetString(filterName)?.ToLowerInvariant()
            ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Indiquez les faces : « {indicesName} » (indices renvoyés par get_solid_topology) ou « {filterName} » " +
                "(top, bottom, sides ou all).");

        var faces = filter switch
        {
            "all" => topology.Faces,
            "top" => topology.Faces.Where(face => face.Normal?.Z >= 1 - 1e-3).ToList(),
            "bottom" => topology.Faces.Where(face => face.Normal?.Z <= -1 + 1e-3).ToList(),
            "sides" => topology.Faces.Where(face => face.Normal is Vector3d normal && Math.Abs(normal.Z) <= 1e-3).ToList(),
            _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Filtre de faces « {filter} » inconnu : top, bottom, sides ou all."),
        };

        return faces.Count > 0
            ? faces
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"Aucune face ne correspond au filtre « {filter} ».");
    }

    /// <summary>Arêtes désignées par indices (« edges ») ou par un filtre (« edgeFilter »).</summary>
    private static List<EdgeInfo> SelectEdges(SolidTopology topology, ArgReader reader)
    {
        if (reader.Has("edges"))
            return ReadIndices(reader, "edges", topology.Edges.Count).Select(index => topology.Edges[index]).ToList();

        var filter = reader.GetString("edgeFilter")?.ToLowerInvariant()
            ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                "Indiquez les arêtes : « edges » (indices renvoyés par get_solid_topology) ou « edgeFilter » " +
                "(vertical, horizontal, top, bottom ou all).");

        IEnumerable<EdgeInfo> edges = filter switch
        {
            "all" => topology.Edges,
            "vertical" => topology.Edges.Where(edge => edge.IsVertical),
            "horizontal" => topology.Edges.Where(edge => edge.IsHorizontal),
            "top" => EdgesOf(topology, topology.Faces.Where(face => face.Normal?.Z >= 1 - 1e-3)),
            "bottom" => EdgesOf(topology, topology.Faces.Where(face => face.Normal?.Z <= -1 + 1e-3)),
            _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Filtre d'arêtes « {filter} » inconnu : vertical, horizontal, top, bottom ou all."),
        };

        var list = edges.ToList();
        return list.Count > 0
            ? list
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"Aucune arête ne correspond au filtre « {filter} ».");
    }

    private static IEnumerable<EdgeInfo> EdgesOf(SolidTopology topology, IEnumerable<FaceInfo> faces) =>
        faces.SelectMany(face => face.Edges).Distinct().Order().Select(index => topology.Edges[index]);

    /// <summary>Indices donnés par un nombre seul ou un tableau de nombres.</summary>
    private static List<int> ReadIndices(ArgReader reader, string name, int count)
    {
        if (!reader.TryGet(name, out var value) || value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Number))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » attend un indice ou un tableau d'indices.");

        var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : new List<JsonElement> { value };
        var indices = new List<int>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var index) || index < 0 || index >= count)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Indice invalide dans « {name} » : entre 0 et {count - 1} (voir get_solid_topology).");

            if (!indices.Contains(index))
                indices.Add(index);
        }

        return indices.Count > 0
            ? indices
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » est vide.");
    }

    /// <summary>
    /// Faces et arêtes du solide, lues par sa représentation B-rep. Les indices suivent l'ordre de parcours d'AutoCAD :
    /// ils restent valables tant que le solide n'est pas modifié.
    /// </summary>
    private static SolidTopology ReadTopology(Solid3d solid)
    {
        var topology = new SolidTopology();
        // Construit depuis l'objet lui-même, le B-rep ne connaît pas le solide dans le dessin et SubentityPath échoue :
        // il faut partir du chemin de l'objet.
        using var brep = new Brep(new FullSubentityPath([solid.ObjectId], new SubentityId(SubentityType.Null, IntPtr.Zero)));

        var edgeIndices = new Dictionary<SubentityId, int>();
        foreach (BrepEdge edge in brep.Edges)
        {
            var path = edge.SubentityPath;
            if (edgeIndices.ContainsKey(path.SubentId))
                continue;

            edgeIndices[path.SubentId] = topology.Edges.Count;
            topology.Edges.Add(DescribeEdge(topology.Edges.Count, path, solid));
        }

        foreach (BrepFace face in brep.Faces)
        {
            var info = DescribeFace(topology.Faces.Count, face, solid);
            try
            {
                foreach (BoundaryLoop loop in face.Loops)
                {
                    foreach (BrepEdge edge in loop.Edges)
                    {
                        if (edgeIndices.TryGetValue(edge.SubentityPath.SubentId, out var index) && !info.Edges.Contains(index))
                        {
                            info.Edges.Add(index);
                            topology.Edges[index].Faces.Add(info.Index);
                        }
                    }
                }
            }
            catch (System.Exception)
            {
                // Boucle sans arête (sphère, tore) : la face reste listée, sans arête.
            }

            topology.Faces.Add(info);
        }

        return topology;
    }

    private static FaceInfo DescribeFace(int index, BrepFace face, Solid3d solid)
    {
        var info = new FaceInfo(index, face.SubentityPath.SubentId);
        try
        {
            info.Area = face.GetArea();
        }
        catch (System.Exception)
        {
        }

        try
        {
            using var surface = face.Surface;
            if (surface is ExternalBoundedSurface bounded)
            {
                info.Type = bounded.IsPlane ? "plane" : bounded.IsCylinder ? "cylinder" : bounded.IsCone ? "cone"
                    : bounded.IsSphere ? "sphere" : bounded.IsTorus ? "torus" : bounded.IsNurbs ? "nurbs" : "other";
                if (bounded.IsPlane)
                {
                    using var baseSurface = bounded.BaseSurface;
                    if (baseSurface is PlanarEntity plane)
                        info.Normal = (face.IsOrientToSurface ? plane.Normal : -plane.Normal).GetNormal();
                }
            }
        }
        catch (System.Exception)
        {
        }

        info.Box = SubentityExtents(solid, face.SubentityPath);
        return info;
    }

    private static EdgeInfo DescribeEdge(int index, FullSubentityPath path, Solid3d solid)
    {
        var info = new EdgeInfo(index, path.SubentId);
        try
        {
            // L'arête extraite comme objet AutoCAD : ligne, arc, cercle, ellipse ou spline.
            using var entity = solid.GetSubentity(path);
            if (entity is Curve curve)
            {
                info.Kind = curve switch { Line => "line", Arc => "arc", Circle => "circle", Ellipse => "ellipse", Spline => "spline", _ => "curve" };
                info.Start = curve.StartPoint;
                info.End = curve.EndPoint;
                info.Length = curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam);
                info.Radius = curve switch { Arc arc => arc.Radius, Circle circle => circle.Radius, _ => null };

                var box = Format.TryGetExtents(curve);
                info.IsHorizontal = box is Extents3d extents && extents.MaxPoint.Z - extents.MinPoint.Z <= Tolerance * Math.Max(1, info.Length ?? 1);
                info.IsVertical = curve is Line line
                    && line.Length > Tolerance
                    && Math.Abs(line.EndPoint.X - line.StartPoint.X) <= Tolerance * Math.Max(1, line.Length)
                    && Math.Abs(line.EndPoint.Y - line.StartPoint.Y) <= Tolerance * Math.Max(1, line.Length);
            }
        }
        catch (System.Exception)
        {
        }

        return info;
    }

    private static Extents3d? SubentityExtents(Solid3d solid, FullSubentityPath path)
    {
        try
        {
            using var entity = solid.GetSubentity(path);
            return entity is null ? null : Format.TryGetExtents(entity);
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    private static Point3d Center(Extents3d box) =>
        box.MinPoint + (box.MaxPoint - box.MinPoint) / 2;

    private sealed class SolidTopology
    {
        public List<FaceInfo> Faces { get; } = [];

        public List<EdgeInfo> Edges { get; } = [];
    }

    private sealed class FaceInfo(int index, SubentityId id)
    {
        public int Index { get; } = index;

        public SubentityId Id { get; } = id;

        public string Type { get; set; } = "other";

        public double? Area { get; set; }

        /// <summary>Normale extérieure des faces planes.</summary>
        public Vector3d? Normal { get; set; }

        public Extents3d? Box { get; set; }

        public List<int> Edges { get; } = [];
    }

    private sealed class EdgeInfo(int index, SubentityId id)
    {
        public int Index { get; } = index;

        public SubentityId Id { get; } = id;

        public string Kind { get; set; } = "curve";

        public double? Length { get; set; }

        public Point3d? Start { get; set; }

        public Point3d? End { get; set; }

        public double? Radius { get; set; }

        public bool IsVertical { get; set; }

        public bool IsHorizontal { get; set; }

        public List<int> Faces { get; } = [];
    }
}
