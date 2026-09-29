using McpMap3D.Shared;
using Xunit;

namespace McpMap3D.Tests;

public class SunCalibrationTests
{
    [Fact]
    public void ParseLocal_DateAndTime_BuildsCivilLocalTime()
    {
        var local = SunCalibration.ParseLocal("2026-06-21", "14:30", null);

        Assert.Equal(new DateTime(2026, 6, 21, 14, 30, 0), local);
        Assert.Equal(DateTimeKind.Unspecified, local.Kind);
    }

    [Fact]
    public void ParseLocal_DateTime_AcceptsIsoAndFrenchHour()
    {
        Assert.Equal(new DateTime(2026, 6, 21, 14, 30, 15), SunCalibration.ParseLocal(null, null, "2026-06-21T14:30:15"));
        Assert.Equal(new DateTime(2026, 1, 15, 8, 5, 0), SunCalibration.ParseLocal("2026-01-15", "8h05", null));
        Assert.Equal(new DateTime(2026, 1, 15, 14, 0, 0), SunCalibration.ParseLocal("2026-01-15", "14h", null));
        Assert.Equal(new DateTime(2026, 6, 21, 9, 15, 0), SunCalibration.ParseLocal(null, "9h15", null, new DateTime(2026, 6, 21, 18, 0, 0)));
    }

    [Fact]
    public void ParseLocal_RejectsAMissingOrInvalidValue()
    {
        var missing = Assert.Throws<ArgumentException>(() => SunCalibration.ParseLocal(null, null, null));
        Assert.Contains("date", missing.Message, StringComparison.OrdinalIgnoreCase);

        var invalid = Assert.Throws<ArgumentException>(() => SunCalibration.ParseLocal("21/06/2026", "14:30", null));
        Assert.Contains("AAAA-MM-JJ", invalid.Message);
    }

    [Fact]
    public void NorthDirection_IsZeroOnTheLambert93CentralMeridian()
    {
        var (x, y) = Lambert93.ToLambert93(3.0, 46.5);
        var (lon1, lat1) = Lambert93.ToWgs84(x, y);
        var (lon2, lat2) = Lambert93.ToWgs84(x, y + 1000);

        var north = SunCalibration.NorthDirectionDegrees(lon1, lat1, lon2, lat2);

        Assert.Equal(0, north, 2);
    }

    [Fact]
    public void NorthDirection_MatchesLambert93ConvergenceWestOfTheCentralMeridian()
    {
        // Place de la Bourse, Bordeaux : ouest du méridien 3° E. La convergence γ = n(λ-λ0)
        // vaut environ -2,59° : le nord de carte est à l'ouest du nord géographique, donc le
        // nord vrai est à +2,59° dans le sens horaire depuis +Y.
        var (x, y) = Lambert93.ToLambert93(-0.570493, 44.841464);
        var (lon1, lat1) = Lambert93.ToWgs84(x, y);
        var (lon2, lat2) = Lambert93.ToWgs84(x, y + 1000);

        var north = SunCalibration.NorthDirectionDegrees(lon1, lat1, lon2, lat2);

        Assert.Equal(2.59, north, 1);
    }

    [Theory]
    [InlineData("2026-01-15", false, 1)]
    [InlineData("2026-06-21", true, 2)]
    [InlineData("2026-03-29", false, 1)] // dernier dimanche de mars, avant 02:00 : encore l'heure d'hiver
    [InlineData("2026-10-25", true, 2)] // dernier dimanche d'octobre, avant 03:00 : encore l'heure d'été
    public void FrenchCivilTime_FollowsEuDaylightSaving(string date, bool daylightSaving, int offset)
    {
        var local = SunCalibration.ParseLocal(date, daylightSaving ? "02:30" : "01:30", null);
        var time = SunCalibration.InferTime("Lambert93", 2.33, 48.86, local);

        Assert.Equal("Paris", time.Zone);
        Assert.Equal(daylightSaving, time.DaylightSaving);
        Assert.Equal(offset, time.UtcOffsetHours);
        Assert.Equal("Europe/Paris", time.Source);
    }

    [Fact]
    public void InferTime_WithoutAFrenchSystem_UsesSolarOffsetAndSaysSo()
    {
        var local = SunCalibration.ParseLocal("2026-06-21", "12:00", null);
        var time = SunCalibration.InferTime("UTM84-32N", 15.2, 45.0, local);

        Assert.Equal("offset", time.Zone);
        Assert.False(time.DaylightSaving);
        Assert.Equal(1, time.UtcOffsetHours);
        Assert.Contains("solaire", time.Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitOffset_WinsOverTheCoordinateSystem()
    {
        var local = SunCalibration.ParseLocal("2026-06-21", "14:00", null);
        var time = SunCalibration.InferTime("Lambert93", 2.33, 48.86, local, utcOffsetHours: 0, daylightSaving: false);

        Assert.Equal("offset", time.Zone);
        Assert.Equal(0, time.UtcOffsetHours);
        Assert.False(time.DaylightSaving);
        Assert.Equal("explicite", time.Source);
    }

    [Theory]
    [InlineData("Lambert93", true)]
    [InlineData("RGF93.Lambert-93", true)]
    [InlineData("RGF93.CC46", true)]
    [InlineData("NTF.Lambert-2e", true)]
    [InlineData("Lambert-II-etendu", true)]
    [InlineData("LAMB2E", true)]
    [InlineData("LambertIII", true)]
    [InlineData("2154", true)]
    [InlineData("EPSG:3946", true)]
    [InlineData("27572", true)]
    [InlineData("Belge72.Lambert", false)]
    [InlineData("Lambert72", false)]
    [InlineData("Lambert2008", false)]
    [InlineData("UTM84-32N", false)]
    [InlineData("LL84", false)]
    [InlineData(null, false)]
    public void IsFrenchSystem_RecognisesOnlyFrenchSystems(string? code, bool french)
    {
        Assert.Equal(french, SunCalibration.IsFrenchSystem(code));
    }

    [Theory]
    [InlineData(4.83, 45.76, true)]   // Lyon
    [InlineData(7.75, 48.58, true)]   // Strasbourg
    [InlineData(1.08, 49.92, true)]   // Dieppe
    [InlineData(1.85, 50.95, true)]   // Calais
    [InlineData(2.37, 51.03, true)]   // Dunkerque
    [InlineData(-1.62, 49.64, true)]  // Cherbourg
    [InlineData(-4.49, 48.39, true)]  // Brest
    [InlineData(8.74, 41.93, true)]   // Ajaccio
    [InlineData(4.35, 50.85, true)]   // Bruxelles, même heure
    [InlineData(-0.13, 51.51, false)] // Londres
    [InlineData(-0.14, 50.82, false)] // Brighton
    [InlineData(1.30, 51.13, false)]  // Douvres
    [InlineData(-5.20, 49.96, false)] // cap Lizard
    [InlineData(-2.10, 49.21, false)] // Jersey
    [InlineData(-9.14, 38.72, false)] // Lisbonne
    [InlineData(-74.0, 40.71, false)] // New York
    public void IsParisTimeArea_CoversFranceButNotEngland(double longitude, double latitude, bool paris)
    {
        Assert.Equal(paris, SunCalibration.IsParisTimeArea(longitude, latitude));
    }

    [Fact]
    public void InferTime_FrenchPlaceInUtmOrWgs84_UsesParisTime()
    {
        var local = SunCalibration.ParseLocal("2026-06-21", "13:45", null);

        var utm = SunCalibration.InferTime("UTM84-31N", 7.75, 48.58, local);
        var wgs = SunCalibration.InferTime("LL84", 4.83, 45.76, local);

        Assert.Equal("Paris", utm.Zone);
        Assert.Equal(2, utm.UtcOffsetHours);
        Assert.Equal("Paris", wgs.Zone);
        Assert.True(wgs.DaylightSaving);
    }
}
