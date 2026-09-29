using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Plugin.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;
using DbSurface = Autodesk.AutoCAD.DatabaseServices.Surface;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Maillages : création à partir de sommets et de faces, lissage, conversions entre maillages, solides et surfaces,
/// et maillage de terrain triangulé à partir de points cotés.
/// </summary>
internal static partial class ModelingTools
{
    private const int MaxMeshVertices = 100_000;
    private const int MaxTerrainPoints = 100_000;
    private const int MaxSmoothLevel = 4;

    public static object CreateMesh(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var vertices = reader.RequirePoints("vertices", minimum: 3);
        if (vertices.Count > MaxMeshVertices)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Trop de sommets ({vertices.Count}) : {MaxMeshVertices} au maximum.");

        var faces = ReadFaces(reader, vertices.Count);
        var mesh = NewMesh(vertices, faces, reader.GetInt("smoothLevel", 0, min: 0, max: MaxSmoothLevel));
        return CreatedMesh(context, mesh, reader);
    }

    /// <summary>Change le niveau de lissage de maillages, comme les commandes LISSERPLUS et LISSERMOINS.</summary>
    public static object SmoothMesh(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        if (!reader.Has("level"))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « level » est obligatoire : de 0 (facettes) à {MaxSmoothLevel}.");

        var level = reader.GetInt("level", 0, min: 0, max: MaxSmoothLevel);
        var results = new List<object>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not SubDMesh)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"L'objet {id.Handle} n'est pas un maillage. Convertissez d'abord solides et surfaces avec convert_mesh (to = mesh).");

            var mesh = (SubDMesh)EditTools.OpenForWrite(id, transaction, context);
            var before = mesh.SmoothLevel;
            try
            {
                // Un niveau par appel ; la boucle est bornée au cas où AutoCAD plafonnerait le lissage (SMOOTHMESHMAXLEV).
                for (var step = 0; step < MaxSmoothLevel && mesh.SmoothLevel != level; step++)
                {
                    if (mesh.SmoothLevel < level)
                        mesh.SubdDivideUp();
                    else
                        mesh.SubdDivideDown();
                }
            }
            catch (AcException ex)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"AutoCAD n'a pas pu lisser le maillage {id.Handle} ({ex.ErrorStatus}) : trop de faces pour ce niveau " +
                    "(variable SMOOTHMESHMAXFACE) ?");
            }

            if (mesh.SmoothLevel != level)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Le maillage {id.Handle} reste au niveau {mesh.SmoothLevel} : la variable SMOOTHMESHMAXLEV limite peut-être le lissage.");

            results.Add(new
            {
                Handle = id.Handle.ToString(),
                LevelBefore = before,
                Level = mesh.SmoothLevel,
                Faces = mesh.NumberOfFaces,
                SmoothedFaces = mesh.NumberOfSubDividedFaces,
            });
        }

        return new { Count = results.Count, Meshes = results };
    }

    /// <summary>
    /// Conversions : maillage vers solide ou surface (CONVSOLIDE, CONVSURF), ou solide, surface et région vers
    /// maillage (MAILLAGELISSE). L'objet converti remplace l'original, sauf avec keepOriginals.
    /// </summary>
    public static object ConvertMesh(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var target = reader.RequireString("to").ToLowerInvariant();
        if (target is not ("solid" or "surface" or "mesh"))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"« to » vaut solid, surface ou mesh, pas « {target} ».");

        var toMesh = target == "mesh";
        string[] misplaced = toMesh ? ["smooth", "optimize"] : ["meshType", "maxDeviation", "maxAngle", "maxEdgeLength"];
        if (misplaced.FirstOrDefault(reader.Has) is { } option)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"« {option} » ne s'applique pas à la conversion vers « {target} ».");

        MeshFaceterData? faceter = toMesh ? ReadFaceter(reader) : null;
        var smooth = reader.GetBool("smooth", false);
        var optimize = reader.GetBool("optimize", true);
        var keepOriginals = reader.GetBool("keepOriginals", false);

        var results = new List<object>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            var source = (Entity)transaction.GetObject(id, OpenMode.ForRead);
            var converted = faceter is { } settings ? MeshOf(source, settings) : AcisOf(source, target == "solid", smooth, optimize);
            converted.SetPropertiesFrom(source);
            var appended = EditTools.Append(context, converted, reader);
            if (!keepOriginals)
                EditTools.OpenForWrite(id, transaction, context).Erase();

            results.Add(new
            {
                Source = id.Handle.ToString(),
                Converted = appended,
                Volume = converted is Solid3d solid ? TryVolume(solid) : null,
                Area = converted is DbSurface surface ? TryNumber(surface.GetArea) : null,
                Vertices = converted is SubDMesh mesh ? mesh.NumberOfVertices : (int?)null,
                Faces = converted is SubDMesh faces ? faces.NumberOfFaces : (int?)null,
            });
        }

        return new { To = target, Count = results.Count, Objects = results, OriginalsKept = keepOriginals };
    }

    /// <summary>
    /// Maillage de terrain : triangulation de Delaunay en plan de points cotés, dont les altitudes sont conservées.
    /// Les points viennent d'un tableau, ou d'objets du dessin (points, blocs, lignes, polylignes) désignés par handle
    /// ou par calque. Les triangles dont un côté dépasse maxEdgeLength en plan sont retirés, pour ne pas combler les
    /// creux du contour.
    /// </summary>
    public static object CreateTerrainMesh(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var points = new List<Point3d>();
        if (reader.Has("points"))
            points.AddRange(reader.RequirePoints("points", minimum: 1));

        if (reader.Has("handles") || reader.Has("layers"))
            points.AddRange(TerrainPoints(context, reader, transaction));

        if (points.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Indiquez les points cotés : « points » ([[x, y, z], …]), ou des objets du dessin par « handles » ou " +
                "« layers » (points, blocs, lignes, polylignes).");

        if (points.Count > MaxTerrainPoints)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Trop de points ({points.Count}) : {MaxTerrainPoints} au maximum.");

        var maxEdgeLength = reader.GetDouble("maxEdgeLength", 0);
        if (maxEdgeLength < 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "« maxEdgeLength » ne peut pas être négatif (0 : aucune limite).");

        var kind = reader.GetString("meshType", "mesh")!.ToLowerInvariant();
        if (kind is not ("mesh" or "polyface"))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"« meshType » vaut mesh ou polyface, pas « {kind} ».");

        Triangulation triangulation;
        try
        {
            triangulation = Delaunay.Triangulate(points.Select(point => point.X).ToArray(), points.Select(point => point.Y).ToArray());
        }
        catch (ArgumentException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, ex.Message);
        }

        // Retrait des triangles trop grands, puis renumérotation des seuls sommets encore utilisés.
        var kept = new List<int>(triangulation.Triangles.Length);
        var removed = 0;
        for (var t = 0; t < triangulation.Count; t++)
        {
            var (a, b, c) = (triangulation.Triangles[3 * t], triangulation.Triangles[3 * t + 1], triangulation.Triangles[3 * t + 2]);
            if (maxEdgeLength > 0 && Math.Max(PlanDistance(points[a], points[b]), Math.Max(PlanDistance(points[b], points[c]), PlanDistance(points[c], points[a]))) > maxEdgeLength)
            {
                removed++;
                continue;
            }

            kept.AddRange([a, b, c]);
        }

        if (kept.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Tous les triangles ont un côté plus long que maxEdgeLength ({maxEdgeLength}) : augmentez-le, ou mettez 0.");

        var renumbered = new Dictionary<int, int>();
        var vertices = new List<Point3d>();
        var corners = new int[kept.Count];
        for (var i = 0; i < kept.Count; i++)
        {
            if (!renumbered.TryGetValue(kept[i], out var index))
            {
                index = vertices.Count;
                renumbered[kept[i]] = index;
                vertices.Add(points[kept[i]]);
            }

            corners[i] = index;
        }

        var (planArea, area) = (0.0, 0.0);
        for (var i = 0; i < corners.Length; i += 3)
        {
            var (u, v) = (vertices[corners[i + 1]] - vertices[corners[i]], vertices[corners[i + 2]] - vertices[corners[i]]);
            planArea += Math.Abs(u.X * v.Y - u.Y * v.X) / 2;
            area += u.CrossProduct(v).Length / 2;
        }

        var appended = kind == "polyface"
            ? AppendPolyFace(context, reader, vertices, corners)
            : EditTools.Append(context, NewMesh(vertices, TriangleFaces(corners), 0), reader);

        return new
        {
            Mesh = appended,
            Points = points.Count,
            Vertices = vertices.Count,
            Triangles = corners.Length / 3,
            DuplicatePoints = triangulation.SkippedPoints > 0 ? triangulation.SkippedPoints : (int?)null,
            RemovedTriangles = removed > 0 ? removed : (int?)null,
            MinZ = Format.Number(vertices.Min(point => point.Z)),
            MaxZ = Format.Number(vertices.Max(point => point.Z)),
            PlanArea = Format.Number(planArea),
            Area = Format.Number(area),
        };
    }

    /// <summary>Faces données comme des listes d'indices de sommets (à partir de 0), au format de SetSubDMesh.</summary>
    private static Int32Collection ReadFaces(ArgReader reader, int vertexCount)
    {
        if (!reader.TryGet("faces", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "« faces » attend un tableau de faces, chacune la liste de ses sommets, par exemple [[0,1,2],[0,2,3]].");

        var faces = new Int32Collection();
        var number = 0;
        foreach (var face in value.EnumerateArray())
        {
            if (face.ValueKind != JsonValueKind.Array || face.GetArrayLength() < 3)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"La face {number} doit lister au moins trois sommets.");

            var corners = new List<int>();
            foreach (var item in face.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var index) || index < 0 || index >= vertexCount)
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"Indice de sommet invalide dans la face {number} : entre 0 et {vertexCount - 1}.");

                if (corners.Contains(index))
                    throw new PipeException(PipeErrorCodes.InvalidParams, $"La face {number} passe deux fois par le sommet {index}.");

                corners.Add(index);
            }

            faces.Add(corners.Count);
            foreach (var index in corners)
                faces.Add(index);

            number++;
        }

        return faces;
    }

    private static Int32Collection TriangleFaces(int[] corners)
    {
        var faces = new Int32Collection(corners.Length / 3 * 4);
        for (var i = 0; i < corners.Length; i += 3)
        {
            faces.Add(3);
            faces.Add(corners[i]);
            faces.Add(corners[i + 1]);
            faces.Add(corners[i + 2]);
        }

        return faces;
    }

    private static SubDMesh NewMesh(IReadOnlyList<Point3d> vertices, Int32Collection faces, int smoothLevel)
    {
        var mesh = new SubDMesh();
        try
        {
            using var points = new Point3dCollection([.. vertices]);
            mesh.SetSubDMesh(points, faces, smoothLevel);
        }
        catch (AcException ex)
        {
            mesh.Dispose();
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"AutoCAD n'a pas pu créer le maillage ({ex.ErrorStatus}) : une arête borde au plus deux faces, que ces " +
                "faces parcourent en sens inverse.");
        }

        return mesh;
    }

    private static object CreatedMesh(ToolContext context, SubDMesh mesh, ArgReader reader)
    {
        var appended = EditTools.Append(context, mesh, reader);
        return new
        {
            Mesh = appended,
            Vertices = mesh.NumberOfVertices,
            Faces = mesh.NumberOfFaces,
            mesh.SmoothLevel,
            mesh.Watertight,
            Volume = mesh.Watertight ? TryNumber(mesh.ComputeVolume) : null,
        };
    }

    /// <summary>
    /// Maillage polyface, lisible par les anciennes versions et la plupart des logiciels : le maillage est ajouté au
    /// dessin avant ses sommets et ses faces, numérotés à partir de 1.
    /// </summary>
    private static object AppendPolyFace(ToolContext context, ArgReader reader, List<Point3d> vertices, int[] corners)
    {
        if (vertices.Count > short.MaxValue)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Un maillage polyface est limité à {short.MaxValue} sommets ({vertices.Count} ici) : utilisez meshType = mesh.");

        var transaction = context.RequireTransaction();
        var polyface = new PolyFaceMesh();
        var appended = EditTools.Append(context, polyface, reader);
        foreach (var point in vertices)
        {
            var vertex = new PolyFaceMeshVertex(point);
            polyface.AppendVertex(vertex);
            transaction.AddNewlyCreatedDBObject(vertex, true);
        }

        for (var i = 0; i < corners.Length; i += 3)
        {
            var face = new FaceRecord((short)(corners[i] + 1), (short)(corners[i + 1] + 1), (short)(corners[i + 2] + 1), 0);
            polyface.AppendFaceRecord(face);
            transaction.AddNewlyCreatedDBObject(face, true);
        }

        return appended;
    }

    /// <summary>Points cotés lus dans le dessin : points, insertions de blocs, extrémités de lignes, sommets de polylignes.</summary>
    private static List<Point3d> TerrainPoints(ToolContext context, ArgReader reader, Transaction transaction)
    {
        var points = new List<Point3d>();
        if (reader.Has("handles"))
        {
            foreach (var id in Handles.Resolve(context, reader))
            {
                var dbObject = transaction.GetObject(id, OpenMode.ForRead);
                if (!AddTerrainPoints(dbObject, transaction, points))
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"L'objet {id.Handle} ({Format.DxfName(dbObject)}) ne porte pas de point coté : points, blocs, lignes " +
                        "ou polylignes attendus.");
            }
        }

        if (reader.Has("layers"))
        {
            var layers = NameFilter.Create(reader.GetStrings("layers"))
                ?? throw new PipeException(PipeErrorCodes.InvalidParams, "« layers » est vide : indiquez un nom de calque ou un motif.");

            string[] classes = ["POINT", "INSERT", "LINE", "LWPOLYLINE", "POLYLINE"];
            var modelSpace = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(context.Database), OpenMode.ForRead);
            foreach (var id in modelSpace)
            {
                if (!classes.Contains(id.ObjectClass.DxfName))
                    continue;

                var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                if (layers.IsMatch(entity.Layer))
                    AddTerrainPoints(entity, transaction, points);

                if (points.Count > MaxTerrainPoints)
                    break;
            }
        }

        return points;
    }

    private static bool AddTerrainPoints(DBObject dbObject, Transaction transaction, List<Point3d> points)
    {
        switch (dbObject)
        {
            case DBPoint point:
                points.Add(point.Position);
                return true;
            case BlockReference block:
                points.Add(block.Position);
                return true;
            case Line line:
                points.Add(line.StartPoint);
                points.Add(line.EndPoint);
                return true;
            case Polyline polyline:
                for (var i = 0; i < polyline.NumberOfVertices; i++)
                    points.Add(polyline.GetPoint3dAt(i));

                return true;
            case Polyline2d polyline:
                // Les sommets de contrôle d'une polyligne lissée ne sont pas sur la courbe : ils sont ignorés.
                foreach (ObjectId vertexId in polyline)
                {
                    var vertex = (Vertex2d)transaction.GetObject(vertexId, OpenMode.ForRead);
                    if (vertex.VertexType != Vertex2dType.SplineControlVertex)
                        points.Add(polyline.VertexPosition(vertex));
                }

                return true;
            case Polyline3d polyline:
                foreach (ObjectId vertexId in polyline)
                {
                    var vertex = (PolylineVertex3d)transaction.GetObject(vertexId, OpenMode.ForRead);
                    if (vertex.VertexType != Vertex3dType.ControlVertex)
                        points.Add(vertex.Position);
                }

                return true;
            default:
                return false;
        }
    }

    private static double PlanDistance(Point3d a, Point3d b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    /// <summary>Réglages de facettes de la conversion en maillage (commande MAILLAGELISSE, variables FACETER*).</summary>
    private static MeshFaceterData ReadFaceter(ArgReader reader)
    {
        var meshType = reader.GetString("meshType", "optimized")!.ToLowerInvariant() switch
        {
            "optimized" => (short)0,
            "quads" => (short)1,
            "triangles" => (short)2,
            var other => throw new PipeException(PipeErrorCodes.InvalidParams,
                $"« meshType » vaut optimized, quads ou triangles, pas « {other} »."),
        };

        var maxAngle = reader.GetDouble("maxAngle", 40);
        if (maxAngle <= 0 || maxAngle > 90)
            throw new PipeException(PipeErrorCodes.InvalidParams, "« maxAngle » doit être compris entre 0 (exclu) et 90 degrés.");

        var maxEdgeLength = reader.GetDouble("maxEdgeLength", 0);
        if (maxEdgeLength < 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "« maxEdgeLength » ne peut pas être négatif (0 : aucune limite).");

        // L'écart maximal par défaut est relatif à la taille de l'objet : il est fixé objet par objet dans MeshOf.
        var maxDeviation = reader.Has("maxDeviation") ? Positive(reader, "maxDeviation") : 0;
        return new MeshFaceterData(maxDeviation, Format.Radians(maxAngle), 2, maxEdgeLength, 15, 5, 5, meshType);
    }

    /// <summary>Maillage d'un solide, d'une surface ou d'une région.</summary>
    private static SubDMesh MeshOf(Entity source, MeshFaceterData faceter)
    {
        if (source is not (Solid3d or DbSurface or Region))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"L'objet {source.Handle} ({Format.DxfName(source)}) n'est ni un solide, ni une surface, ni une région.");

        if (faceter.FaceterDevSurface <= 0)
        {
            // Écart par défaut : 1 % de la diagonale de l'objet, pour un nombre de faces indépendant de son échelle.
            var size = Format.TryGetExtents(source) is Extents3d box ? box.MinPoint.DistanceTo(box.MaxPoint) : 1;
            faceter.FaceterDevSurface = 0.01 * Math.Max(size, Tolerance);
        }

        MeshDataCollection data;
        try
        {
            data = SubDMesh.GetObjectMesh(source, faceter);
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu mailler l'objet {source.Handle} ({ex.ErrorStatus}).");
        }

        var mesh = new SubDMesh();
        try
        {
            using var vertices = data.VertexArray;
            mesh.SetSubDMesh(vertices, data.FaceArray, 0);
        }
        catch (AcException ex)
        {
            mesh.Dispose();
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu créer le maillage de {source.Handle} ({ex.ErrorStatus}).");
        }

        return mesh;
    }

    /// <summary>Solide ou surface tiré d'un maillage ; lisse ou à facettes, facettes coplanaires fusionnées ou non.</summary>
    private static Entity AcisOf(Entity source, bool toSolid, bool smooth, bool optimize)
    {
        if (source is not SubDMesh mesh)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"L'objet {source.Handle} ({Format.DxfName(source)}) n'est pas un maillage : seuls les maillages se convertissent " +
                "en solide ou en surface. Pour l'inverse, indiquez to = mesh.");

        Entity? converted;
        try
        {
            converted = toSolid ? mesh.ConvertToSolid(smooth, optimize) : mesh.ConvertToSurface(smooth, optimize);
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, toSolid
                ? $"AutoCAD n'a pas pu convertir le maillage {mesh.Handle} en solide ({ex.ErrorStatus}) : le maillage doit être " +
                  $"fermé, sans trou ni face qui se recoupe (fermé : {(mesh.Watertight ? "oui" : "non")})."
                : $"AutoCAD n'a pas pu convertir le maillage {mesh.Handle} en surface ({ex.ErrorStatus}).");
        }

        return converted ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a rien renvoyé pour le maillage {mesh.Handle}.");
    }
}
