using McpMap3D.Plugin.Geometry;
using Xunit;

namespace McpMap3D.Tests;

public class DelaunayTests
{
    [Fact]
    public void Triangulate_SquareWithCenter_GivesFourTriangles()
    {
        var result = Delaunay.Triangulate([0, 10, 10, 0, 5], [0, 0, 10, 10, 5]);

        Assert.Equal(4, result.Count);
        AssertValid([0, 10, 10, 0, 5], [0, 0, 10, 10, 5], result, expectedArea: 100);
    }

    [Fact]
    public void Triangulate_RandomPoints_IsDelaunayAndCoversTheHull()
    {
        var random = new Random(42);
        var xs = Enumerable.Range(0, 2000).Select(_ => random.NextDouble() * 1000).ToArray();
        var ys = Enumerable.Range(0, 2000).Select(_ => random.NextDouble() * 500).ToArray();

        var result = Delaunay.Triangulate(xs, ys);

        AssertValid(xs, ys, result, expectedArea: HullArea(xs, ys));
        Assert.Equal(2 * xs.Length - 2 - HullSize(xs, ys), result.Count);
        AssertEmptyCircles(xs, ys, result);
    }

    [Fact]
    public void Triangulate_RegularGrid_HandlesCocircularPoints()
    {
        // Grille de 30 × 30 points au pas de 1 : chaque maille a quatre points sur un même cercle.
        var xs = new List<double>();
        var ys = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            for (var j = 0; j < 30; j++)
            {
                xs.Add(i);
                ys.Add(j);
            }
        }

        var result = Delaunay.Triangulate(xs, ys);

        Assert.Equal(2 * 29 * 29, result.Count);
        AssertValid(xs, ys, result, expectedArea: 29 * 29);
    }

    [Fact]
    public void Triangulate_Lambert93Coordinates_KeepsPrecision()
    {
        // Points au pas de 0,5 m autour de Bordeaux en Lambert-93.
        var random = new Random(7);
        var xs = Enumerable.Range(0, 400).Select(i => 417947.0 + (i % 20) * 0.5 + random.NextDouble() * 0.01).ToArray();
        var ys = Enumerable.Range(0, 400).Select(i => 6422188.0 + (i / 20) * 0.5 + random.NextDouble() * 0.01).ToArray();

        var result = Delaunay.Triangulate(xs, ys);

        AssertValid(xs, ys, result, expectedArea: HullArea(xs, ys));
        Assert.Equal(0, result.SkippedPoints);
    }

    [Fact]
    public void Triangulate_DuplicatePoints_AreSkipped()
    {
        var result = Delaunay.Triangulate([0, 10, 0, 10, 10, 5], [0, 0, 10, 10, 0, 5]);

        Assert.Equal(1, result.SkippedPoints);
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void Triangulate_PointsOnTheHull_AreKept()
    {
        // Points posés sur les côtés du carré, insérés après ses coins.
        double[] xs = [0, 10, 10, 0, 5, 10, 5, 0, 5];
        double[] ys = [0, 0, 10, 10, 0, 5, 10, 5, 5];

        var result = Delaunay.Triangulate(xs, ys);

        Assert.Equal(8, result.Count);
        AssertValid(xs, ys, result, expectedArea: 100);
    }

    [Fact]
    public void Triangulate_CollinearPoints_Throws()
    {
        Assert.Throws<ArgumentException>(() => Delaunay.Triangulate([0, 1, 2, 3], [0, 1, 2, 3]));
    }

    [Fact]
    public void Triangulate_ManyPoints_IsFast()
    {
        var random = new Random(3);
        var xs = Enumerable.Range(0, 100_000).Select(_ => random.NextDouble() * 5000).ToArray();
        var ys = Enumerable.Range(0, 100_000).Select(_ => random.NextDouble() * 5000).ToArray();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = Delaunay.Triangulate(xs, ys);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.Elapsed.TotalSeconds:F1} s");
        AssertValid(xs, ys, result, expectedArea: HullArea(xs, ys));
    }

    /// <summary>Triangles orientés, sans chevauchement : chaque côté borde au plus deux triangles, dans des sens opposés.</summary>
    private static void AssertValid(IReadOnlyList<double> xs, IReadOnlyList<double> ys, Triangulation result, double expectedArea)
    {
        var edges = new HashSet<(int, int)>();
        double area = 0;
        for (var t = 0; t < result.Count; t++)
        {
            var (a, b, c) = (result.Triangles[3 * t], result.Triangles[3 * t + 1], result.Triangles[3 * t + 2]);
            var doubleArea = (xs[b] - xs[a]) * (ys[c] - ys[a]) - (ys[b] - ys[a]) * (xs[c] - xs[a]);
            Assert.True(doubleArea > 0, $"triangle {t} ({a}, {b}, {c}) mal orienté ou plat : {doubleArea}");
            area += doubleArea / 2;
            foreach (var edge in new[] { (a, b), (b, c), (c, a) })
                Assert.True(edges.Add(edge), $"côté {edge} présent deux fois dans le même sens");
        }

        Assert.Equal(expectedArea, area, 6);
    }

    private static void AssertEmptyCircles(IReadOnlyList<double> xs, IReadOnlyList<double> ys, Triangulation result)
    {
        for (var t = 0; t < result.Count; t++)
        {
            var (a, b, c) = (result.Triangles[3 * t], result.Triangles[3 * t + 1], result.Triangles[3 * t + 2]);
            for (var d = 0; d < xs.Count; d++)
            {
                if (d == a || d == b || d == c)
                    continue;

                double Ax = xs[a] - xs[d], Ay = ys[a] - ys[d], Bx = xs[b] - xs[d], By = ys[b] - ys[d], Cx = xs[c] - xs[d], Cy = ys[c] - ys[d];
                var det = (Ax * Ax + Ay * Ay) * (Bx * Cy - Cx * By) - (Bx * Bx + By * By) * (Ax * Cy - Cx * Ay) + (Cx * Cx + Cy * Cy) * (Ax * By - Bx * Ay);
                Assert.True(det <= 1e-6, $"le point {d} est dans le cercle du triangle {t}");
            }
        }
    }

    private static List<int> Hull(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        // Enveloppe convexe (chaîne monotone), sans les points alignés sur les côtés.
        var order = Enumerable.Range(0, xs.Count).OrderBy(i => xs[i]).ThenBy(i => ys[i]).ToList();
        double Cross(int o, int a, int b) => (xs[a] - xs[o]) * (ys[b] - ys[o]) - (ys[a] - ys[o]) * (xs[b] - xs[o]);
        var hull = new List<int>();
        foreach (var pass in new[] { order, Enumerable.Reverse(order).ToList() })
        {
            var start = hull.Count;
            foreach (var i in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], i) <= 0)
                    hull.RemoveAt(hull.Count - 1);

                hull.Add(i);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull;
    }

    private static int HullSize(IReadOnlyList<double> xs, IReadOnlyList<double> ys) => Hull(xs, ys).Count;

    private static double HullArea(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        // Coordonnées relatives au premier sommet, pour ne pas perdre les décimales en Lambert-93.
        var hull = Hull(xs, ys);
        var (x0, y0) = (xs[hull[0]], ys[hull[0]]);
        double area = 0;
        for (var i = 0; i < hull.Count; i++)
        {
            var (a, b) = (hull[i], hull[(i + 1) % hull.Count]);
            area += (xs[a] - x0) * (ys[b] - y0) - (xs[b] - x0) * (ys[a] - y0);
        }

        return area / 2;
    }
}
