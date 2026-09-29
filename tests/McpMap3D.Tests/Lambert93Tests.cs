using McpMap3D.Shared;
using Xunit;

namespace McpMap3D.Tests;

public class Lambert93Tests
{
    [Fact]
    public void ToLambert93_BordeauxPlaceDeLaBourse_MatchesOfficialIgn()
    {
        // Coordonnées WGS84 de la Place de la Bourse à Bordeaux
        var lon = -0.570493;
        var lat = 44.841464;

        var (x, y) = Lambert93.ToLambert93(lon, lat);

        // Valeurs officielles IGN / EPSG:2154
        Assert.Equal(417947.32, x, 2);
        Assert.Equal(6422188.75, y, 2);
    }

    [Fact]
    public void ToLambert93_Paris12RueDeLaPaix_MatchesOfficialCoordinates()
    {
        // 12 Rue de la Paix 75002 Paris
        var lon = 2.331303;
        var lat = 48.869141;

        var (x, y) = Lambert93.ToLambert93(lon, lat);

        Assert.Equal(650947.59, x, 2);
        Assert.Equal(6863442.50, y, 2);
    }

    [Fact]
    public void ToLambert93_Strasbourg_MatchesOfficialCoordinates()
    {
        // Cathédrale de Strasbourg (Est)
        var lon = 7.7508;
        var lat = 48.5818;

        var (x, y) = Lambert93.ToLambert93(lon, lat);

        Assert.Equal(1050210.82, x, 2);
        Assert.Equal(6841826.01, y, 2);
    }

    [Fact]
    public void ToLambert93_Brest_MatchesOfficialCoordinates()
    {
        // Port de Brest (Ouest)
        var lon = -4.4860;
        var lat = 48.3830;

        var (x, y) = Lambert93.ToLambert93(lon, lat);

        Assert.Equal(146562.49, x, 2);
        Assert.Equal(6835442.80, y, 2);
    }

    [Theory]
    [InlineData(-0.570493, 44.841464)] // Bordeaux
    [InlineData(2.2945, 48.8584)]       // Paris
    [InlineData(7.7508, 48.5818)]       // Strasbourg
    [InlineData(-4.4860, 48.3830)]      // Brest
    [InlineData(5.3698, 43.2965)]       // Marseille
    [InlineData(4.8357, 45.7640)]       // Lyon
    [InlineData(1.4442, 43.6047)]       // Toulouse
    [InlineData(3.0573, 50.6292)]       // Lille
    public void RoundTrip_SubMillimetricAccuracy(double origLon, double origLat)
    {
        var (x, y) = Lambert93.ToLambert93(origLon, origLat);
        var (roundLon, roundLat) = Lambert93.ToWgs84(x, y);

        Assert.Equal(origLon, roundLon, 6);
        Assert.Equal(origLat, roundLat, 6);
    }

    [Fact]
    public void IsLambert93Range_MetropolitanFrance_ReturnsTrue()
    {
        // Emprise de Bordeaux
        Assert.True(Lambert93.IsLambert93Range(417000, 6420000, 419000, 6425000));
        // Emprise de Paris
        Assert.True(Lambert93.IsLambert93Range(640000, 6850000, 660000, 6870000));
    }

    [Fact]
    public void IsLambert93Range_LocalOrInvalid_ReturnsFalse()
    {
        // Coordonnées locales CAO centrées sur l'origine
        Assert.False(Lambert93.IsLambert93Range(0, 0, 100, 100));
        // Coordonnées négatives
        Assert.False(Lambert93.IsLambert93Range(-100, -100, 0, 0));
        // Coordonnées UTM (ex: Guadeloupe X ~ 650000, Y ~ 1790000)
        Assert.False(Lambert93.IsLambert93Range(640000, 1780000, 660000, 1800000));
    }

    [Fact]
    public void InvalidInputs_ReturnsNaN()
    {
        var (x1, y1) = Lambert93.ToLambert93(double.NaN, 45.0);
        Assert.True(double.IsNaN(x1) && double.IsNaN(y1));

        var (x2, y2) = Lambert93.ToLambert93(0.0, 95.0);
        Assert.True(double.IsNaN(x2) && double.IsNaN(y2));

        var (lon1, lat1) = Lambert93.ToWgs84(double.NaN, 6000000.0);
        Assert.True(double.IsNaN(lon1) && double.IsNaN(lat1));
    }
}
