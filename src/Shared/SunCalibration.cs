using System.Globalization;
using System.Text.RegularExpressions;

namespace McpMap3D.Shared;

/// <summary>
/// Calage du soleil sur le lieu du dessin : heure civile, fuseau, et angle entre le nord de la
/// projection (+Y) et le nord géographique. Aucune dépendance à AutoCAD : le plug-in applique le résultat
/// aux propriétés Latitude, Longitude, NorthDirection et TimeZone du dessin.
/// </summary>
public static class SunCalibration
{
    public readonly record struct TimeChoice(string Zone, double UtcOffsetHours, bool DaylightSaving, string Source);

    /// <summary>
    /// Heure civile locale. date au format AAAA-MM-JJ, time en HH:mm, HH:mm:ss ou 14h30.
    /// dateTime (AAAA-MM-JJTHH:mm) remplace les deux. Une heure seule garde le jour currentDay, s'il est connu.
    /// </summary>
    public static DateTime ParseLocal(string? date, string? time, string? dateTime, DateTime? currentDay = null)
    {
        if (!string.IsNullOrWhiteSpace(dateTime))
            return ParseDateTime(dateTime.Trim());

        DateTime day;
        if (string.IsNullOrWhiteSpace(date))
        {
            if (string.IsNullOrWhiteSpace(time) || currentDay is null)
                throw new ArgumentException("Indiquez « date » (AAAA-MM-JJ) et « time » (HH:mm), ou « dateTime ».");
            day = currentDay.Value.Date;
        }
        else if (!DateTime.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
        {
            throw new ArgumentException($"Date « {date} » : attendu AAAA-MM-JJ, par exemple 2026-06-21.");
        }

        var clock = string.IsNullOrWhiteSpace(time) ? TimeSpan.Zero : ParseClock(time.Trim());
        return DateTime.SpecifyKind(day.Date + clock, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// Angle NORTHDIRECTION d'AutoCAD, en degrés : du +Y du dessin (nord de la carte) vers le nord
    /// géographique, dans le sens horaire. Les deux points sont l'ancre et le même point décalé vers +Y.
    /// </summary>
    public static double NorthDirectionDegrees(double lon1, double lat1, double lon2, double lat2)
    {
        var bearing = BearingDegrees(lon1, lat1, lon2, lat2);
        return NormalizeSigned(-bearing);
    }

    /// <summary>
    /// Fuseau à appliquer. Un décalage explicite gagne. Un système français (Lambert-93, RGF93, NTF), ou un lieu
    /// situé dans la zone à l'heure de Paris quel que soit le système (UTM, WGS84…), utilise l'heure de Paris,
    /// heure d'été européenne comprise. Sinon, décalage solaire arrondi (un fuseau par 15° de longitude), sans
    /// heure d'été : c'est une approximation, signalée.
    /// </summary>
    public static TimeChoice InferTime(
        string? coordinateSystemCode,
        double longitude,
        double latitude,
        DateTime local,
        double? utcOffsetHours = null,
        bool? daylightSaving = null)
    {
        if (utcOffsetHours is double hours)
        {
            if (hours is < -12 or > 14)
                throw new ArgumentException("Le décalage UTC doit être compris entre -12 et 14 heures.");

            return new TimeChoice("offset", hours, daylightSaving ?? false, "explicite");
        }

        if (IsFrenchSystem(coordinateSystemCode) || IsParisTimeArea(longitude, latitude))
        {
            var saving = daylightSaving ?? IsEuropeanDaylightSaving(local);
            return new TimeChoice("Paris", saving ? 2 : 1, saving, "Europe/Paris");
        }

        var solar = Math.Clamp(Math.Round(longitude / 15.0), -12, 14);
        return new TimeChoice(
            "offset",
            solar,
            daylightSaving ?? false,
            "solaire (un fuseau par 15° de longitude, sans heure d'été civile)");
    }

    /// <summary>
    /// Systèmes français, à l'heure légale de Paris : Lambert-93 et zones coniques CC42 à CC50 (RGF93),
    /// Lambert I à IV et II étendu (NTF), et leurs codes EPSG. Les Lambert d'autres pays (belge, etc.) n'en font pas partie.
    /// </summary>
    public static bool IsFrenchSystem(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var text = code.Trim().ToLowerInvariant();
        return text.Contains("rgf93", StringComparison.Ordinal)
            || text.Contains("ntf", StringComparison.Ordinal)
            || text.Contains("france", StringComparison.Ordinal)
            || FrenchLambert.IsMatch(text)
            || FrenchEpsg.IsMatch(text);
    }

    private static readonly Regex FrenchLambert = new(
        @"(^|[^a-z])lamb(ert)?[-_. ]?(93|2e|ii[-_ ]?(e|et|etendu|carto)|i{1,3}|iv|[1-4]c?)($|[^a-z0-9])",
        RegexOptions.CultureInvariant);

    private static readonly Regex FrenchEpsg = new(
        @"^(epsg:)?(2154|39(4[2-9]|50)|2757[1-4]|2756[1-4])$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// France métropolitaine et Corse, avec les pays voisins compris dans le même rectangle, tous à l'heure
    /// d'Europe centrale (Belgique, Luxembourg, ouest de l'Allemagne, Suisse, nord de l'Italie et de l'Espagne).
    /// Le sud de l'Angleterre et les îles Anglo-Normandes, à l'heure de Londres, sont exclus.
    /// </summary>
    public static bool IsParisTimeArea(double longitude, double latitude)
    {
        if (latitude is < 41.3 or > 51.2 || longitude is < -5.3 or > 9.7)
            return false;

        // Angleterre : côte sud jusqu'à Hastings, puis le Kent (Dungeness, Douvres, Margate).
        if (latitude > 49.9 && longitude < 0.9 || latitude > 50.85 && longitude < 1.5)
            return false;

        // Jersey, Guernesey, Aurigny.
        return !(latitude is > 49.1 and < 49.8 && longitude < -2.0);
    }

    /// <summary>
    /// Heure d'été européenne : du dernier dimanche de mars à 02:00 (heure d'hiver) au dernier dimanche
    /// d'octobre à 03:00 (heure d'été). Le 29 mars à 01:30 est encore l'hiver ; le 25 octobre à 02:30 est encore l'été.
    /// </summary>
    public static bool IsEuropeanDaylightSaving(DateTime local)
    {
        var start = LastSunday(local.Year, 3).AddHours(2);
        var end = LastSunday(local.Year, 10).AddHours(3);
        return local >= start && local < end;
    }

    private static double BearingDegrees(double lon1, double lat1, double lon2, double lat2)
    {
        var φ1 = lat1 * Math.PI / 180.0;
        var φ2 = lat2 * Math.PI / 180.0;
        var Δλ = (lon2 - lon1) * Math.PI / 180.0;
        var y = Math.Sin(Δλ) * Math.Cos(φ2);
        var x = Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(Δλ);
        return Math.Atan2(y, x) * 180.0 / Math.PI;
    }

    private static double NormalizeSigned(double degrees)
    {
        var value = degrees % 360.0;
        if (value > 180)
            value -= 360;
        if (value < -180)
            value += 360;
        return value;
    }

    private static DateTime LastSunday(int year, int month)
    {
        var day = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        while (day.DayOfWeek != DayOfWeek.Sunday)
            day = day.AddDays(-1);
        return day;
    }

    private static DateTime ParseDateTime(string text)
    {
        var separator = text.IndexOf('T') is > 0 and var t ? t : text.IndexOf(' ');
        if (separator < 0)
            throw new ArgumentException($"Date et heure « {text} » : attendu AAAA-MM-JJTHH:mm.");

        var day = ParseLocal(text[..separator], null, null);
        return day.Date + ParseClock(text[(separator + 1)..]);
    }

    private static TimeSpan ParseClock(string text)
    {
        var normalized = text.Replace('h', ':').Replace('H', ':');
        if (normalized.EndsWith(':'))
            normalized += "00";
        var formats = new[] { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" };
        if (TimeSpan.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, out var clock))
            return clock;

        throw new ArgumentException($"Heure « {text} » : attendu HH:mm, HH:mm:ss ou 14h30.");
    }
}

