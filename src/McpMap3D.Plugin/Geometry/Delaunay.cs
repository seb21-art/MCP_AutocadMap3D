namespace McpMap3D.Plugin.Geometry;

/// <summary>Triangles d'une triangulation, trois indices de points par triangle, et points écartés comme doublons.</summary>
internal sealed record Triangulation(int[] Triangles, int SkippedPoints)
{
    public int Count => Triangles.Length / 3;
}

/// <summary>
/// Triangulation de Delaunay en plan (X, Y) d'un semis de points, par insertion successive (Bowyer-Watson).
/// L'extérieur de l'enveloppe convexe est fermé par des triangles « fantômes » reliés à un sommet à l'infini :
/// pas de super-triangle, donc une enveloppe exacte et pas d'erreurs d'arrondi sur des sommets lointains.
/// Aucune dépendance à AutoCAD, pour être testée hors d'AutoCAD.
/// </summary>
internal sealed class Delaunay
{
    private const int Infinite = -1;

    private readonly double[] _x;
    private readonly double[] _y;

    // Trois sommets par triangle, dans le sens trigonométrique, et le voisin opposé à chacun d'eux.
    private readonly List<int> _vertices = [];
    private readonly List<int> _neighbors = [];
    private readonly List<bool> _alive = [];
    private readonly Stack<int> _free = new();
    private int _last;

    // Marques de la cavité en cours, par numéro d'insertion, pour éviter de vider un tableau à chaque point.
    private readonly List<int> _stamp = [];
    private int _currentStamp;

    private Delaunay(double[] x, double[] y)
    {
        _x = x;
        _y = y;
    }

    /// <summary>
    /// Triangule les points donnés. Les triangles renvoyés sont orientés dans le sens trigonométrique vu de dessus.
    /// Les points confondus en plan avec un point déjà inséré sont écartés et comptés dans SkippedPoints.
    /// </summary>
    /// <exception cref="ArgumentException">Moins de trois points, ou tous alignés.</exception>
    public static Triangulation Triangulate(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        if (xs.Count != ys.Count)
            throw new ArgumentException("Autant de X que de Y sont attendus.");

        var count = xs.Count;
        if (count < 3)
            throw new ArgumentException("Il faut au moins trois points.");

        // Coordonnées ramenées près de l'origine : en Lambert-93 (X ≈ 400 000, Y ≈ 6 400 000), les tests
        // d'orientation et de cercle perdraient sinon leurs décimales.
        var (minX, minY) = (xs.Min(), ys.Min());
        var x = new double[count];
        var y = new double[count];
        for (var i = 0; i < count; i++)
        {
            x[i] = xs[i] - minX;
            y[i] = ys[i] - minY;
        }

        return new Delaunay(x, y).Run();
    }

    private Triangulation Run()
    {
        var order = HilbertOrder();

        // Premier triangle : les deux premiers points distincts et le premier point non aligné avec eux.
        var first = order[0];
        var second = order.Skip(1).FirstOrDefault(i => _x[i] != _x[first] || _y[i] != _y[first], -1);
        var third = second < 0 ? -1 : order.FirstOrDefault(i => Orient(first, second, i) != 0, -1);
        if (third < 0)
            throw new ArgumentException("Les points sont tous alignés (ou confondus) : aucune surface à trianguler.");

        CreateFirstTriangle(first, second, third);
        foreach (var point in order)
        {
            if (point != first && point != second && point != third)
                Insert(point);
        }

        var triangles = new List<int>();
        for (var t = 0; t < _alive.Count; t++)
        {
            if (_alive[t] && !IsGhost(t))
                triangles.AddRange([_vertices[3 * t], _vertices[3 * t + 1], _vertices[3 * t + 2]]);
        }

        // Un point écarté à l'insertion, ou absorbé par un point inséré après lui à moins d'un arrondi près,
        // n'est sommet d'aucun triangle.
        var skipped = _x.Length - triangles.Distinct().Count();
        return new Triangulation([.. triangles], skipped);
    }

    private void CreateFirstTriangle(int a, int b, int c)
    {
        if (Orient(a, b, c) < 0)
            (b, c) = (c, b);

        var inner = NewTriangle(a, b, c);
        // Un fantôme par côté, collé au côté extérieur : (b, a, ∞) pour le côté a → b.
        var ghostAb = NewTriangle(b, a, Infinite);
        var ghostBc = NewTriangle(c, b, Infinite);
        var ghostCa = NewTriangle(a, c, Infinite);
        LinkAll([inner, ghostAb, ghostBc, ghostCa]);
        _last = inner;
    }

    /// <summary>Insère un point, sauf s'il est confondu en plan avec un sommet existant.</summary>
    private void Insert(int point)
    {
        var start = Locate(point);
        if (start < 0 || !InConflict(start, point))
            return;

        var cavity = Cavity(start, point);
        if (cavity is null)
            return;

        // Nouveaux triangles : le point relié à chaque côté du bord de la cavité, dans le même sens que ce côté.
        var created = new List<int>(cavity.Count + 2);
        var byStart = new Dictionary<int, int>();
        foreach (var (from, to, outside, owner) in Boundary(cavity).ToList())
        {
            var triangle = NewTriangle(from, to, point);
            _neighbors[3 * triangle + 2] = outside;
            for (var k = 0; k < 3; k++)
            {
                if (_neighbors[3 * outside + k] == owner)
                    _neighbors[3 * outside + k] = triangle;
            }

            byStart[from] = triangle;
            created.Add(triangle);
        }

        // Deux nouveaux triangles voisins partagent un côté issu du point : (a, b, p) touche (b, c, p) par b-p.
        foreach (var triangle in created)
        {
            var next = byStart[_vertices[3 * triangle + 1]];
            _neighbors[3 * triangle] = next;
            _neighbors[3 * next + 1] = triangle;
        }

        foreach (var triangle in cavity)
        {
            _alive[triangle] = false;
            _free.Push(triangle);
        }

        _last = created.FirstOrDefault(t => !IsGhost(t), created[0]);
    }

    /// <summary>
    /// Triangles dont le cercle circonscrit contient le point, d'un seul tenant autour du triangle de départ.
    /// Les arrondis peuvent y inclure un triangle dont un côté du bord ne « voit » pas le point : il est retiré,
    /// jusqu'à ce que tout le bord soit visible, sans quoi les nouveaux triangles se chevaucheraient.
    /// </summary>
    private List<int>? Cavity(int start, int point)
    {
        _currentStamp++;
        var cavity = new List<int> { start };
        Mark(start);
        for (var i = 0; i < cavity.Count; i++)
        {
            var triangle = cavity[i];
            for (var k = 0; k < 3; k++)
            {
                var neighbor = _neighbors[3 * triangle + k];
                if (!IsMarked(neighbor) && InConflict(neighbor, point))
                {
                    Mark(neighbor);
                    cavity.Add(neighbor);
                }
            }
        }

        while (true)
        {
            var hidden = Boundary(cavity)
                .Where(edge => edge.From != Infinite && edge.To != Infinite && Orient(edge.From, edge.To, point) <= 0)
                .Select(edge => edge.Owner)
                .ToHashSet();
            if (hidden.Count == 0)
                return cavity;

            if (hidden.Contains(start))
                return null;

            // On retire les triangles fautifs, puis on ne garde que la partie reliée au triangle de départ.
            var kept = cavity.Where(t => !hidden.Contains(t)).ToHashSet();
            _currentStamp++;
            cavity = [start];
            Mark(start);
            for (var i = 0; i < cavity.Count; i++)
            {
                for (var k = 0; k < 3; k++)
                {
                    var neighbor = _neighbors[3 * cavity[i] + k];
                    if (kept.Contains(neighbor) && !IsMarked(neighbor))
                    {
                        Mark(neighbor);
                        cavity.Add(neighbor);
                    }
                }
            }
        }
    }

    /// <summary>Côtés du bord de la cavité, orientés comme dans leur triangle, avec le triangle extérieur.</summary>
    private IEnumerable<(int From, int To, int Outside, int Owner)> Boundary(List<int> cavity)
    {
        foreach (var triangle in cavity)
        {
            for (var k = 0; k < 3; k++)
            {
                var neighbor = _neighbors[3 * triangle + k];
                if (!IsMarked(neighbor))
                    yield return (_vertices[3 * triangle + (k + 1) % 3], _vertices[3 * triangle + (k + 2) % 3], neighbor, triangle);
            }
        }
    }

    /// <summary>
    /// Triangle qui contient le point, par une marche depuis le dernier triangle créé ; un fantôme si le point est
    /// hors de l'enveloppe. Si la marche tourne en rond (cas dégénérés), recherche exhaustive.
    /// </summary>
    private int Locate(int point)
    {
        var triangle = _last;
        if (!_alive[triangle] || IsGhost(triangle))
            triangle = FirstFinite();

        var steps = 0;
        var limit = 4 * _alive.Count + 100;
        while (steps++ < limit)
        {
            if (IsGhost(triangle))
                return triangle;

            var next = -1;
            var offset = steps % 3;
            for (var i = 0; i < 3 && next < 0; i++)
            {
                var k = (offset + i) % 3;
                if (Orient(_vertices[3 * triangle + (k + 1) % 3], _vertices[3 * triangle + (k + 2) % 3], point) < 0)
                    next = _neighbors[3 * triangle + k];
            }

            if (next < 0)
                return triangle;

            triangle = next;
        }

        for (var t = 0; t < _alive.Count; t++)
        {
            if (_alive[t] && InConflict(t, point))
                return t;
        }

        return -1;
    }

    /// <summary>
    /// Le point est-il dans le cercle circonscrit du triangle ? Pour un fantôme, ce « cercle » devient le demi-plan
    /// extérieur à son côté fini, plus le côté lui-même (point posé sur l'enveloppe).
    /// </summary>
    private bool InConflict(int triangle, int point)
    {
        var (a, b, c) = (_vertices[3 * triangle], _vertices[3 * triangle + 1], _vertices[3 * triangle + 2]);
        if (a != Infinite && b != Infinite && c != Infinite)
            return InCircle(a, b, c, point) > 0;

        // Côté fini du fantôme, dans l'ordre du triangle : l'extérieur de l'enveloppe est à sa gauche.
        var (u, v) = a == Infinite ? (b, c) : b == Infinite ? (c, a) : (a, b);
        var side = Orient(u, v, point);
        if (side != 0)
            return side > 0;

        // Aligné avec le côté : en conflit seulement entre ses deux extrémités.
        var dot = (_x[point] - _x[u]) * (_x[v] - _x[u]) + (_y[point] - _y[u]) * (_y[v] - _y[u]);
        var length = (_x[v] - _x[u]) * (_x[v] - _x[u]) + (_y[v] - _y[u]) * (_y[v] - _y[u]);
        return dot > 0 && dot < length;
    }

    private double Orient(int a, int b, int c) =>
        (_x[b] - _x[a]) * (_y[c] - _y[a]) - (_y[b] - _y[a]) * (_x[c] - _x[a]);

    /// <summary>Positif si d est dans le cercle passant par a, b, c (dans le sens trigonométrique).</summary>
    private double InCircle(int a, int b, int c, int d)
    {
        var (adx, ady) = (_x[a] - _x[d], _y[a] - _y[d]);
        var (bdx, bdy) = (_x[b] - _x[d], _y[b] - _y[d]);
        var (cdx, cdy) = (_x[c] - _x[d], _y[c] - _y[d]);
        var ad = adx * adx + ady * ady;
        var bd = bdx * bdx + bdy * bdy;
        var cd = cdx * cdx + cdy * cdy;
        return adx * (bdy * cd - bd * cdy) - ady * (bdx * cd - bd * cdx) + ad * (bdx * cdy - bdy * cdx);
    }

    private int NewTriangle(int a, int b, int c)
    {
        int triangle;
        if (_free.Count > 0)
        {
            triangle = _free.Pop();
            _alive[triangle] = true;
        }
        else
        {
            triangle = _alive.Count;
            _vertices.AddRange([0, 0, 0]);
            _neighbors.AddRange([-1, -1, -1]);
            _alive.Add(true);
            _stamp.Add(0);
        }

        (_vertices[3 * triangle], _vertices[3 * triangle + 1], _vertices[3 * triangle + 2]) = (a, b, c);
        (_neighbors[3 * triangle], _neighbors[3 * triangle + 1], _neighbors[3 * triangle + 2]) = (-1, -1, -1);
        _stamp[triangle] = 0;
        return triangle;
    }

    /// <summary>Relie entre eux des triangles qui partagent des côtés (initialisation).</summary>
    private void LinkAll(IReadOnlyList<int> triangles)
    {
        var edges = new Dictionary<(int, int), (int Triangle, int Opposite)>();
        foreach (var triangle in triangles)
        {
            for (var k = 0; k < 3; k++)
            {
                var from = _vertices[3 * triangle + (k + 1) % 3];
                var to = _vertices[3 * triangle + (k + 2) % 3];
                if (edges.Remove((to, from), out var other))
                {
                    _neighbors[3 * triangle + k] = other.Triangle;
                    _neighbors[3 * other.Triangle + other.Opposite] = triangle;
                }
                else
                {
                    edges[(from, to)] = (triangle, k);
                }
            }
        }
    }

    private bool IsGhost(int triangle) =>
        _vertices[3 * triangle] == Infinite || _vertices[3 * triangle + 1] == Infinite || _vertices[3 * triangle + 2] == Infinite;

    private int FirstFinite()
    {
        for (var t = 0; t < _alive.Count; t++)
        {
            if (_alive[t] && !IsGhost(t))
                return t;
        }

        throw new InvalidOperationException("Triangulation vide.");
    }

    private void Mark(int triangle) => _stamp[triangle] = _currentStamp;

    private bool IsMarked(int triangle) => _stamp[triangle] == _currentStamp;

    /// <summary>Ordre d'insertion suivant une courbe de Hilbert : chaque point est proche du précédent, la marche est courte.</summary>
    private int[] HilbertOrder()
    {
        const int side = 1 << 16;
        var (maxX, maxY) = (_x.Max(), _y.Max());
        var scale = (side - 1) / Math.Max(Math.Max(maxX, maxY), 1e-12);
        var keys = new long[_x.Length];
        for (var i = 0; i < _x.Length; i++)
            keys[i] = HilbertIndex(side, (int)(_x[i] * scale), (int)(_y[i] * scale));

        var order = Enumerable.Range(0, _x.Length).ToArray();
        Array.Sort(keys, order);
        return order;
    }

    private static long HilbertIndex(int side, int x, int y)
    {
        long index = 0;
        for (var s = side / 2; s > 0; s /= 2)
        {
            var rx = (x & s) > 0 ? 1 : 0;
            var ry = (y & s) > 0 ? 1 : 0;
            index += (long)s * s * ((3 * rx) ^ ry);
            if (ry == 0)
            {
                if (rx == 1)
                {
                    x = side - 1 - x;
                    y = side - 1 - y;
                }

                (x, y) = (y, x);
            }
        }

        return index;
    }
}
