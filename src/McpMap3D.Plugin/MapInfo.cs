using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Gis.Map;
using Autodesk.Gis.Map.Project;

namespace McpMap3D.Plugin;

/// <summary>
/// Accès isolé à l'API Map 3D : ManagedMapApi n'est résolue qu'à l'appel de ces méthodes,
/// le plug-in reste donc chargeable même si elle est absente.
/// </summary>
internal static class MapInfo
{
    public static string Describe()
    {
        try
        {
            return DescribeCore();
        }
        catch (Exception ex) when (IsApiMissing(ex))
        {
            return "non disponible (ManagedMapApi introuvable)";
        }
        catch (Exception ex)
        {
            return $"erreur : {ex.Message}";
        }
    }

    public static object DescribeProject(Database database)
    {
        try
        {
            return DescribeProjectCore(database);
        }
        catch (Exception ex) when (IsApiMissing(ex))
        {
            return new { Available = false, Reason = "ManagedMapApi introuvable" };
        }
        catch (Exception ex)
        {
            return new { Available = false, Reason = ex.Message };
        }
    }

    /// <summary>Projet Map du dessin. Les objets renvoyés appartiennent à Map 3D : ne pas les libérer.</summary>
    public static ProjectModel RequireProject(Database database)
    {
        var project = HostMapApplicationServices.Application.GetProjectForDB(database);
        return project ?? throw new Shared.PipeException(Shared.PipeErrorCodes.MapUnavailable,
            "Aucun projet Map 3D n'est associé à ce dessin.");
    }

    public static bool IsAvailable
    {
        get
        {
            try
            {
                return IsAvailableCore();
            }
            catch (Exception ex) when (IsApiMissing(ex))
            {
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsApiMissing(Exception ex) =>
        ex is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAvailableCore() => HostMapApplicationServices.Application is not null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string DescribeCore() =>
        HostMapApplicationServices.Application is null ? "non initialisée" : "disponible";

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object DescribeProjectCore(Database database)
    {
        var project = HostMapApplicationServices.Application.GetProjectForDB(database);
        if (project is null)
            return new { Available = true, ProjectActive = false };

        return new
        {
            Available = true,
            ProjectActive = project.Active,
            CoordinateSystem = string.IsNullOrEmpty(project.Projection) ? null : project.Projection,
            OdTableCount = project.ODTables.TablesCount,
        };
    }
}
