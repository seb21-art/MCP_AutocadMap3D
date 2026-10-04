using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>Accès au dessin requis par un outil.</summary>
internal enum DrawingAccess
{
    /// <summary>Aucun dessin requis (ping…).</summary>
    None,

    /// <summary>Lecture : verrou en lecture et transaction.</summary>
    Read,

    /// <summary>Écriture : verrou en écriture nommé, annulable en une étape par U.</summary>
    Write,

    /// <summary>Opération sur le document sans transaction pré-ouverte (LayoutManager…).</summary>
    Document,

    /// <summary>
    /// Travail long hors du thread principal (conversion de fichier par un programme externe) : AutoCAD reste
    /// disponible pendant ce temps. Aucun objet AutoCAD ne doit y être lu ni modifié.
    /// </summary>
    Background,
}

/// <summary>Contexte passé à un outil, sur le thread principal d'AutoCAD.</summary>
internal sealed class ToolContext(Document? document, Transaction? transaction)
{
    public Document? Document { get; } = document;

    public Transaction? Transaction { get; } = transaction;

    public Document RequireDocument() =>
        Document ?? throw new PipeException(PipeErrorCodes.NoDocument, "Aucun dessin ouvert dans AutoCAD.");

    public Database Database => RequireDocument().Database;

    public Editor Editor => RequireDocument().Editor;

    public Transaction RequireTransaction() =>
        Transaction ?? throw new InvalidOperationException("Cet outil a été déclaré sans accès au dessin.");

    private List<Action>? _afterCommit;

    /// <summary>
    /// Second temps d'un outil d'écriture, exécuté une fois la transaction validée, quand les objets créés existent
    /// vraiment dans le dessin : même commande, donc même étape d'annulation. Ignoré si l'outil échoue avant ;
    /// une exception levée ici fait échouer l'appel.
    /// </summary>
    public void AfterCommit(Action action) => (_afterCommit ??= []).Add(action);

    public void RunAfterCommit()
    {
        foreach (var action in _afterCommit ?? [])
            action();
    }

    /// <summary>
    /// Résultat calculé après le second temps (AfterCommit), à la place de celui que l'outil a renvoyé : pour un outil
    /// dont l'opération n'a lieu qu'une fois la transaction validée. Le calcul ouvre sa propre transaction.
    /// </summary>
    public Func<object?>? ResultAfterCommit { get; set; }
}

/// <summary>
/// Un outil reçoit les paramètres JSON de la requête et renvoie un objet sérialisable
/// composé uniquement de données simples (jamais d'objet AutoCAD).
/// </summary>
internal delegate object? ToolHandler(ToolContext context, JsonElement? args);

internal sealed record ToolDefinition(string Name, DrawingAccess Access, ToolHandler Handler);

internal sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Names => _tools.Keys;

    public ToolRegistry Add(string name, DrawingAccess access, ToolHandler handler)
    {
        _tools.Add(name, new ToolDefinition(name, access, handler));
        return this;
    }

    public bool TryGet(string name, out ToolDefinition tool) => _tools.TryGetValue(name, out tool!);

    public static ToolRegistry CreateDefault() =>
        new ToolRegistry()
            .Add("ping", DrawingAccess.None, SystemTools.Ping)
            .Add("_undo", DrawingAccess.None, SystemTools.Undo)
            .Add("list_drawings", DrawingAccess.None, DrawingTools.ListDrawings)
            .Add("new_drawing", DrawingAccess.None, DrawingTools.NewDrawing)
            .Add("open_drawing", DrawingAccess.None, DrawingTools.OpenDrawing)
            .Add("save_drawing", DrawingAccess.None, DrawingTools.SaveDrawing)
            .Add("close_drawing", DrawingAccess.None, DrawingTools.CloseDrawing)
            .Add("get_drawing_info", DrawingAccess.Read, ReadTools.GetDrawingInfo)
            .Add("list_layers", DrawingAccess.Read, ReadTools.ListLayers)
            .Add("list_entities", DrawingAccess.Read, ReadTools.ListEntities)
            .Add("create_layer", DrawingAccess.Write, EditTools.CreateLayer)
            .Add("create_line", DrawingAccess.Write, EditTools.CreateLine)
            .Add("create_polyline", DrawingAccess.Write, EditTools.CreatePolyline)
            .Add("create_text", DrawingAccess.Write, EditTools.CreateText)
            .Add("move_entities", DrawingAccess.Write, EditTools.MoveEntities)
            .Add("change_layer", DrawingAccess.Write, EditTools.ChangeLayer)
            .Add("erase_entities", DrawingAccess.Write, EditTools.EraseEntities)
            .Add("set_entity_properties", DrawingAccess.Write, EditTools.SetEntityProperties)
            .Add("create_circle", DrawingAccess.Write, DraftingTools.CreateCircle)
            .Add("create_arc", DrawingAccess.Write, DraftingTools.CreateArc)
            .Add("create_ellipse", DrawingAccess.Write, DraftingTools.CreateEllipse)
            .Add("create_spline", DrawingAccess.Write, DraftingTools.CreateSpline)
            .Add("create_rectangle", DrawingAccess.Write, DraftingTools.CreateRectangle)
            .Add("create_mtext", DrawingAccess.Write, DraftingTools.CreateMText)
            .Add("create_hatch", DrawingAccess.Write, DraftingTools.CreateHatch)
            .Add("create_dimension", DrawingAccess.Write, DraftingTools.CreateDimension)
            .Add("list_blocks", DrawingAccess.Read, BlockTools.ListBlocks)
            .Add("create_block", DrawingAccess.Write, BlockTools.CreateBlock)
            .Add("insert_block", DrawingAccess.Write, BlockTools.InsertBlock)
            .Add("copy_entities", DrawingAccess.Write, ModifyTools.CopyEntities)
            .Add("mirror_entities", DrawingAccess.Write, ModifyTools.MirrorEntities)
            .Add("scale_entities", DrawingAccess.Write, ModifyTools.ScaleEntities)
            .Add("offset_entities", DrawingAccess.Write, ModifyTools.OffsetEntities)
            .Add("edit_text", DrawingAccess.Write, ModifyTools.EditText)
            .Add("explode_entities", DrawingAccess.Write, ModifyTools.ExplodeEntities)
            .Add("create_leader", DrawingAccess.Write, DraftingTools.CreateLeader)
            .Add("array_rectangular", DrawingAccess.Write, ArrayTools.ArrayRectangular)
            .Add("array_polar", DrawingAccess.Write, ArrayTools.ArrayPolar)
            .Add("trim_entities", DrawingAccess.Write, TrimExtendTools.TrimEntities)
            .Add("extend_entities", DrawingAccess.Write, TrimExtendTools.ExtendEntities)
            .Add("create_point", DrawingAccess.Write, DraftingTools.CreatePoint)
            .Add("create_polygon", DrawingAccess.Write, DraftingTools.CreatePolygon)
            .Add("create_revision_cloud", DrawingAccess.Write, DraftingTools.CreateRevisionCloud)
            .Add("create_wipeout", DrawingAccess.Write, DraftingTools.CreateWipeout)
            .Add("create_boundary", DrawingAccess.Write, DraftingTools.CreateBoundary)
            .Add("create_table", DrawingAccess.Write, DraftingTools.CreateTable)
            .Add("list_text_styles", DrawingAccess.Read, TextStyleTools.ListTextStyles)
            .Add("set_text_style", DrawingAccess.Write, TextStyleTools.SetTextStyle)
            .Add("join_entities", DrawingAccess.Write, ModifyTools.JoinEntities)
            .Add("break_entities", DrawingAccess.Write, ModifyTools.BreakEntities)
            .Add("edit_polyline", DrawingAccess.Write, ModifyTools.EditPolyline)
            .Add("stretch_entities", DrawingAccess.Write, ModifyTools.StretchEntities)
            .Add("set_draw_order", DrawingAccess.Write, ModifyTools.SetDrawOrder)
            .Add("array_path", DrawingAccess.Write, ArrayTools.ArrayPath)
            .Add("get_intersections", DrawingAccess.Read, MeasureTools.GetIntersections)
            .Add("measure_curve", DrawingAccess.Read, MeasureTools.MeasureCurve)
            .Add("list_dimension_styles", DrawingAccess.Read, DimensionStyleTools.ListDimensionStyles)
            .Add("set_dimension_style", DrawingAccess.Write, DimensionStyleTools.SetDimensionStyle)
            .Add("set_dimension_format", DrawingAccess.Write, DimensionStyleTools.SetDimensionFormat)
            .Add("get_dimension_format", DrawingAccess.Read, DimensionStyleTools.GetDimensionFormat)
            .Add("fillet", DrawingAccess.Write, CornerTools.Fillet)
            .Add("chamfer", DrawingAccess.Write, CornerTools.Chamfer)
            .Add("list_linetypes", DrawingAccess.Read, ReadTools.ListLinetypes)
            .Add("create_box", DrawingAccess.Write, ModelingTools.CreateBox)
            .Add("create_wedge", DrawingAccess.Write, ModelingTools.CreateWedge)
            .Add("create_cylinder", DrawingAccess.Write, ModelingTools.CreateCylinder)
            .Add("create_sphere", DrawingAccess.Write, ModelingTools.CreateSphere)
            .Add("create_torus", DrawingAccess.Write, ModelingTools.CreateTorus)
            .Add("create_pyramid", DrawingAccess.Write, ModelingTools.CreatePyramid)
            .Add("create_3d_polyline", DrawingAccess.Write, ModelingTools.Create3dPolyline)
            .Add("create_helix", DrawingAccess.Write, ModelingTools.CreateHelix)
            .Add("extrude", DrawingAccess.Write, ModelingTools.Extrude)
            .Add("revolve", DrawingAccess.Write, ModelingTools.Revolve)
            .Add("boolean_solids", DrawingAccess.Write, ModelingTools.BooleanSolids)
            .Add("rotate_entities", DrawingAccess.Write, ModelingTools.RotateEntities)
            .Add("get_solid_properties", DrawingAccess.Read, ModelingTools.GetSolidProperties)
            .Add("set_view", DrawingAccess.Write, ModelingTools.SetView)
            .Add("capture_view", DrawingAccess.Read, ModelingTools.CaptureView)
            .Add("create_region", DrawingAccess.Write, ModelingTools.CreateRegion)
            .Add("slice_solid", DrawingAccess.Write, ModelingTools.SliceSolid)
            .Add("get_section", DrawingAccess.Write, ModelingTools.GetSection)
            .Add("loft", DrawingAccess.Write, ModelingTools.Loft)
            .Add("mirror_3d", DrawingAccess.Write, ModifyTools.MirrorEntities3d)
            .Add("align_3d", DrawingAccess.Write, ModelingTools.Align3d)
            .Add("check_interference", DrawingAccess.Write, ModelingTools.CheckInterference)
            .Add("buildings_to_solids", DrawingAccess.Write, ModelingTools.BuildingsToSolids)
            .Add("get_solid_topology", DrawingAccess.Read, ModelingTools.GetSolidTopology)
            .Add("fillet_edges", DrawingAccess.Write, ModelingTools.FilletEdges)
            .Add("chamfer_edges", DrawingAccess.Write, ModelingTools.ChamferEdges)
            .Add("shell_solid", DrawingAccess.Write, ModelingTools.ShellSolid)
            .Add("edit_solid_faces", DrawingAccess.Write, ModelingTools.EditSolidFaces)
            .Add("thicken_surface", DrawingAccess.Write, ModelingTools.ThickenSurface)
            .Add("separate_solid", DrawingAccess.Write, ModelingTools.SeparateSolid)
            .Add("imprint_solid", DrawingAccess.Write, ModelingTools.ImprintSolid)
            .Add("clean_solid", DrawingAccess.Write, ModelingTools.CleanSolid)
            .Add("create_surface", DrawingAccess.Write, ModelingTools.CreateSurface)
            .Add("sculpt_solid", DrawingAccess.Write, ModelingTools.SculptSolid)
            .Add("project_curves", DrawingAccess.Write, ModelingTools.ProjectCurves)
            .Add("create_mesh", DrawingAccess.Write, ModelingTools.CreateMesh)
            .Add("convert_mesh", DrawingAccess.Write, ModelingTools.ConvertMesh)
            .Add("smooth_mesh", DrawingAccess.Write, ModelingTools.SmoothMesh)
            .Add("create_terrain_mesh", DrawingAccess.Write, ModelingTools.CreateTerrainMesh)
            .Add("export_stl", DrawingAccess.Read, ModelingTools.ExportStl)
            .Add("export_sat", DrawingAccess.Read, ModelingTools.ExportSat)
            .Add("import_sat", DrawingAccess.Write, ModelingTools.ImportSat)
            .Add("_convert_3d_model", DrawingAccess.Background, ModelingTools.ConvertModel)
            .Add("_insert_3d_model", DrawingAccess.Write, ModelingTools.InsertModel)
            .Add("get_coordinate_system", DrawingAccess.Read, CoordinateSystemTools.GetCoordinateSystem)
            .Add("search_coordinate_systems", DrawingAccess.Read, CoordinateSystemTools.SearchCoordinateSystems)
            .Add("set_coordinate_system", DrawingAccess.Write, CoordinateSystemTools.SetCoordinateSystem)
            .Add("list_ucs", DrawingAccess.Read, UcsTools.ListUcs)
            .Add("set_ucs", DrawingAccess.Write, UcsTools.SetUcs)
            .Add("delete_ucs", DrawingAccess.Write, UcsTools.DeleteUcs)
            .Add("convert_ucs_points", DrawingAccess.Read, UcsTools.ConvertUcsPoints)
            .Add("list_xrefs", DrawingAccess.Read, XrefTools.ListXrefs)
            .Add("attach_xref", DrawingAccess.Write, XrefTools.AttachXref)
            .Add("detach_xrefs", DrawingAccess.Write, XrefTools.DetachXrefs)
            .Add("reload_xrefs", DrawingAccess.Write, XrefTools.ReloadXrefs)
            .Add("unload_xrefs", DrawingAccess.Write, XrefTools.UnloadXrefs)
            .Add("bind_xrefs", DrawingAccess.Write, XrefTools.BindXrefs)
            .Add("edit_xref", DrawingAccess.Write, XrefTools.EditXref)
            .Add("list_od_tables", DrawingAccess.Read, MapTools.ListOdTables)
            .Add("get_od_records", DrawingAccess.Read, MapTools.GetOdRecords)
            .Add("set_od_value", DrawingAccess.Write, MapTools.SetOdValue)
            .Add("_create_od_table", DrawingAccess.Write, MapTools.CreateOdTable)
            .Add("_fdo_diagnostic", DrawingAccess.Read, FdoTools.Diagnostic)
            .Add("list_layouts", DrawingAccess.Read, LayoutTools.ListLayouts)
            .Add("set_current_layout", DrawingAccess.Document, LayoutTools.SetCurrentLayout)
            .Add("create_layout", DrawingAccess.Document, LayoutTools.CreateLayout)
            .Add("rename_layout", DrawingAccess.Document, LayoutTools.RenameLayout)
            .Add("delete_layout", DrawingAccess.Document, LayoutTools.DeleteLayout)
            .Add("list_viewports", DrawingAccess.Read, LayoutTools.ListViewports)
            .Add("create_viewport", DrawingAccess.Write, LayoutTools.CreateViewport)
            .Add("set_viewport", DrawingAccess.Write, LayoutTools.SetViewport)
            .Add("get_drawing_extents_lonlat", DrawingAccess.Read, CoordinateSystemTools.GetDrawingExtentsLonLat)
            .Add("import_cadastre_geometry", DrawingAccess.Write, CadastreTools.ImportCadastreGeometry)
            .Add("list_plot_devices", DrawingAccess.Read, PlotTools.ListPlotDevices)
            .Add("list_plot_styles", DrawingAccess.Read, PlotTools.ListPlotStyles)
            .Add("export_pdf", DrawingAccess.Document, PlotTools.ExportPdf)
            .Add("plot_drawing", DrawingAccess.Document, PlotTools.PlotDrawing)
            .Add("list_materials", DrawingAccess.Read, SceneTools.ListMaterials)
            .Add("set_material", DrawingAccess.Write, SceneTools.SetMaterial)
            .Add("assign_material", DrawingAccess.Write, SceneTools.AssignMaterial)
            .Add("list_lights", DrawingAccess.Read, SceneTools.ListLights)
            .Add("set_light", DrawingAccess.Write, SceneTools.SetLight)
            .Add("delete_lights", DrawingAccess.Write, SceneTools.DeleteLights)
            .Add("get_sun", DrawingAccess.Read, SceneTools.GetSun)
            .Add("set_sun", DrawingAccess.Write, SceneTools.SetSun)
            .Add("get_view_ambiance", DrawingAccess.Read, SceneTools.GetViewAmbiance)
            .Add("set_view_ambiance", DrawingAccess.Write, SceneTools.SetViewAmbiance)
            .Add("list_fdo_providers", DrawingAccess.Read, FdoTools.ListProviders)
            .Add("list_fdo_connections", DrawingAccess.Read, FdoTools.ListConnections)
            .Add("connect_fdo", DrawingAccess.Write, FdoTools.Connect)
            .Add("describe_fdo", DrawingAccess.Read, FdoTools.Describe)
            .Add("add_fdo_layer", DrawingAccess.Write, FdoTools.AddLayer)
            .Add("list_fdo_layers", DrawingAccess.Read, FdoTools.ListLayers)
            .Add("query_fdo", DrawingAccess.Read, FdoTools.Query)
            .Add("set_fdo_layer", DrawingAccess.Write, FdoTools.SetLayer)
            .Add("remove_fdo_layer", DrawingAccess.Write, FdoTools.RemoveLayer)
            .Add("disconnect_fdo", DrawingAccess.Write, FdoTools.Disconnect)
            .Add("get_fdo_selection", DrawingAccess.Read, FdoTools.GetSelection)
            .Add("import_gis", DrawingAccess.Write, GisTools.ImportGis)
            .Add("export_gis", DrawingAccess.Document, GisTools.ExportGis);
}
