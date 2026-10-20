using System.Runtime.InteropServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace McpMap3D.Plugin;

/// <summary>
/// Accès ADS natif d'AutoCAD, comme (entget) et (entmod) : passer par acdbEntGet fait réellement appeler le
/// dxfOutFields de l'objet, y compris pour un objet personnalisé (Covadis…), ce que l'API .NET ne permet pas.
/// Les fonctions d'entité sont exportées sous leur nom C par accore.dll ; acdbGetAdsName, propre à chaque version
/// (acdb25.dll pour 2026, acdb26.dll pour 2027), est chargée à la demande.
/// </summary>
internal static class NativeAds
{
    public const int RtNorm = 5100;

    [StructLayout(LayoutKind.Sequential)]
    public struct AdsName
    {
        public IntPtr First;
        public IntPtr Second;
    }

    [DllImport("accore.dll", EntryPoint = "acdbEntGet", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr EntGet(ref AdsName name);

    [DllImport("accore.dll", EntryPoint = "acdbEntGetX", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr EntGetX(ref AdsName name, IntPtr applications);

    [DllImport("accore.dll", EntryPoint = "acdbEntMod", CallingConvention = CallingConvention.Cdecl)]
    private static extern int EntMod(IntPtr values);

    [DllImport("accore.dll", EntryPoint = "acdbEntUpd", CallingConvention = CallingConvention.Cdecl)]
    private static extern int EntUpd(ref AdsName name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetAdsNameFunction(out AdsName name, ObjectId id);

    private static readonly Lazy<GetAdsNameFunction?> GetAdsName = new(LoadGetAdsName);

    private static GetAdsNameFunction? LoadGetAdsName()
    {
        // ErrorStatus acdbGetAdsName(ads_name&, AcDbObjectId) : nom décoré C++, dans la DLL de base de données de la version.
        const string Export = "?acdbGetAdsName@@YA?AW4ErrorStatus@Acad@@AEAY01_JVAcDbObjectId@@@Z";
        var library = $"acdb{AcApp.Version.Major}.dll";
        if (!NativeLibrary.TryLoad(library, typeof(NativeAds).Assembly, null, out var handle)
            || !NativeLibrary.TryGetExport(handle, Export, out var address))
            return null;
        return Marshal.GetDelegateForFunctionPointer<GetAdsNameFunction>(address);
    }

    /// <summary>Nom d'entité ADS d'un objet du dessin.</summary>
    public static AdsName NameOf(ObjectId id)
    {
        if (GetAdsName.Value is { } function)
        {
            if (function(out var name, id) == 0)
                return name;
            throw new Shared.PipeException(Shared.PipeErrorCodes.Internal, $"acdbGetAdsName a refusé l'objet {id.Handle}.");
        }

        // Repli si l'export n'est pas trouvé : un nom ADS est l'adresse de l'objet dans le dessin, suivie de zéro.
        return new AdsName { First = id.OldIdPtr, Second = IntPtr.Zero };
    }

    /// <summary>
    /// Données DXF de l'objet, comme (entget). Avec applications, les données étendues des applications nommées
    /// (« * » : toutes), comme (entget e '("*")). Null si AutoCAD refuse l'objet.
    /// </summary>
    public static ResultBuffer? Get(ObjectId id, IReadOnlyList<string>? applications)
    {
        var name = NameOf(id);
        IntPtr pointer;
        if (applications is { Count: > 0 })
        {
            using var apps = new ResultBuffer(applications.Select(app => new TypedValue((int)Autodesk.AutoCAD.Runtime.LispDataType.Text, app)).ToArray());
            pointer = EntGetX(ref name, apps.UnmanagedObject);
        }
        else
        {
            pointer = EntGet(ref name);
        }

        return pointer == IntPtr.Zero ? null : ResultBuffer.Create(pointer, true);
    }

    /// <summary>Applique des données DXF modifiées (entmod), puis met l'affichage à jour (entupd). Code ADS en retour.</summary>
    public static int Modify(ObjectId id, ResultBuffer values)
    {
        var status = EntMod(values.UnmanagedObject);
        if (status != RtNorm)
            return status;
        var name = NameOf(id);
        EntUpd(ref name);
        return status;
    }
}
