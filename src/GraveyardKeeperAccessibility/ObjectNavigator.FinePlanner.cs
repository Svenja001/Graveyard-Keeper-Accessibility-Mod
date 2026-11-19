using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace GraveyardKeeperAccessibility;

// ---- Fine world planner -----------------------------------------------------
//
// The road network (graph 0) has 76-unit cells and does not know the level geometry the player
// collides with. The swamp's walkways are 0.7-1 tile wide: a 76-unit grid cannot represent them at
// all (worked out offline from level3 on 2026-11-19 — see logs-kept/tools), so no tuning of graph 0
// ever got a walk to Hexenland or the witch. The fine player graph (graph 2) is only ever scanned a
// few tiles around the player, and physics cannot see switched-off chunks.
//
// This planner keeps its own 16-unit grid of the whole map:
//   - level geometry, rasterised once from collider SHAPE DATA (works for switched-off chunks),
//     each wall grown by the player's body radius so a free cell is a spot the body's centre fits;
//   - per plan, the world objects near the route (fences, buildings, trees, blockades, closed
//     gates) and every crossing the mod has found closed, stamped on a copy of the search window.
// A* runs on a worker thread; the answer is picked up by PumpFinePlan on the main thread and handed
// to the game's path follower like any other route. Nothing found -> the old road-network path.

internal static partial class ObjectNavigator
{
    private readonly struct FineShape
    {
        public readonly Vector2[] Pts;
        public readonly bool Closed;
        public FineShape(Vector2[] pts, bool closed) { Pts = pts; Closed = closed; }
    }

    private const float FineCell = 16f;
    /// The player's body is a 14.4-unit circle; a hair under it, since the walk is kinematic and a
    /// graze costs nothing while a pass closed by rounding costs the journey.
    private const float FineBodyRadius = 13f;
    private const int FineMaxWindowCells = 5000000;
    private const float FineMinMargin = 40f * TileSize;
    private const float FineMaxMargin = 150f * TileSize;
    /// How far a blocked start or goal may be moved to the nearest free cell.
    private const int FineSnapCells = 24;
    /// A goal the grid cannot enter is still walked to when the start reaches this close to it
    /// (2.5 tiles). Further off, a "no path" is a real one.
    private const int FineNearEnoughCells = 15;

    private static bool[] _fineStatic;
    private static int _fineW, _fineH;
    private static Vector2 _fineOrigin;   // world position of cell (0,0)'s lower-left corner

    private sealed class FinePlanJob
    {
        public int Token;
        public Vector2 From, To;
        public bool[] Grid; public int W, H, Ox, Oy;   // window, and its offset in the static grid
        public int Start, Goal;
        public List<int> Cells;   // result, window indices, start..goal
        public int Expanded;
        public long Ms;
        /// No path: the blocked cells of the thinnest barrier between the start's and the goal's
        /// ground, in crossing order, and the free cell nearest the goal that the start reaches.
        public List<int> Barrier;
        public int Frontier = -1;
        /// The route ends at the nearest reachable spot, a short way from the goal.
        public bool ShortOfGoal;
        public string Error;
        public volatile bool Done;
    }

    private static FinePlanJob _fineJob;
    private static int _fineToken;

    /// The road route now being asked for is a fallback because the fine grid, which sees every
    /// wall, found no way at all. Such a road route is only believed if it is not a tour of the map.
    private static bool _fineFoundNoPath;

    /// A road route this many times the straight distance, plus the slack, after the fine grid
    /// found nothing, is not walked: on 2026-11-19 the walk to Hexenland set off on a 900-point
    /// loop through the village and closed toll gates, and twice got stuck there.
    private const float FallbackDetourStretch = 2.5f;
    private const float FallbackDetourSlack = 40f * TileSize;

    /// Called with a fresh road route. True when it was refused (and the walk stopped).
    private static bool RefuseFallbackTour(List<Vector3> route, Vector2 from, Vector2 to)
    {
        if (!_fineFoundNoPath) return false;
        _fineFoundNoPath = false;
        float len = PathLength(route);
        float straight = Vector2.Distance(from, to);
        if (len <= straight * FallbackDetourStretch + FallbackDetourSlack) return false;
        _log?.LogWarning($"[NAVIGATOR] Road route is {len / TileSize:F0} tiles for {straight / TileSize:F0} straight " +
                         "and the fine grid found no way at all; not walking it");
        var label = _longWalkTarget.Label;
        string blocker = null;
        try
        {
            if (_fineBarrierWgo != null)
                blocker = ScreenReader.StripNguiCodes(InteractionDetector.GetObjectLabel(_fineBarrierWgo) ?? "").Trim();
        }
        catch { }
        StopLongWalk(announce: false);
        if (!string.IsNullOrEmpty(blocker))
        {
            var p = _fineBarrierWgo.pos;
            var me = MainGame.me?.player?.pos ?? p;
            ScreenReader.Say(Loc.Fmt("nav.no_walkable_way_blocked", label, blocker,
                                     CompassDirection(me, p), DistanceText(Vector2.Distance(me, p))),
                             interrupt: true);
        }
        else
            ScreenReader.Say(Loc.Fmt("nav.no_walkable_way", label), interrupt: true);
        return true;
    }

    /// The object in the thinnest barrier of the last failed fine search (what closes the way).
    private static WorldGameObject _fineBarrierWgo;

    private sealed class WgoShapeCache
    {
        public Vector2 Pos;
        public string ObjId;
        public List<FineShape> Shapes;
    }
    private static readonly Dictionary<WorldGameObject, WgoShapeCache> _wgoShapes = new Dictionary<WorldGameObject, WgoShapeCache>();
    private static readonly List<WorldGameObject> _fineSnapshot = new List<WorldGameObject>(4096);

    // ---- static grid --------------------------------------------------------

    private static void BuildFineStatic(Pathfinding.GridGraph gg, List<FineShape> shapes)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Bounds of the road network: every outdoor (and house-interior) spot lies inside it.
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var nd in new[] { gg.nodes[0], gg.nodes[gg.width - 1], gg.nodes[gg.nodes.Length - gg.width], gg.nodes[gg.nodes.Length - 1] })
            {
                var p = (Vector3)nd.position;
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            minX -= gg.nodeSize; minY -= gg.nodeSize; maxX += gg.nodeSize; maxY += gg.nodeSize;
            _fineOrigin = new Vector2(minX, minY);
            _fineW = Mathf.CeilToInt((maxX - minX) / FineCell);
            _fineH = Mathf.CeilToInt((maxY - minY) / FineCell);
            var grid = new bool[_fineW * _fineH];
            foreach (var s in shapes) StampShape(grid, _fineW, _fineH, 0, 0, s);
            _fineStatic = grid;
            int blocked = 0;
            for (int i = 0; i < grid.Length; i++) if (grid[i]) blocked++;
            _log?.LogInfo($"[NAVIGATOR] Fine world grid: {_fineW}x{_fineH} cells of {FineCell}u, " +
                          $"{blocked} blocked by {shapes.Count} wall shapes, {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            _fineStatic = null;
            _log?.LogWarning($"[NAVIGATOR] Fine world grid failed: {ex.Message}");
        }
    }

    /// Block every cell (of a window at offset ox,oy in the static grid) whose centre lies inside
    /// the shape or within the body radius of its outline.
    private static void StampShape(bool[] grid, int w, int h, int ox, int oy, FineShape s)
    {
        var pts = s.Pts;
        if (pts == null || pts.Length < 2) return;
        float r = FineBodyRadius, r2 = r * r;
        int n = pts.Length;
        int segs = s.Closed ? n : n - 1;

        // Outline, thickened.
        for (int k = 0; k < segs; k++)
        {
            var a = pts[k]; var b = pts[(k + 1) % n];
            int x0 = CellX(Mathf.Min(a.x, b.x) - r) - ox, x1 = CellX(Mathf.Max(a.x, b.x) + r) - ox;
            int y0 = CellY(Mathf.Min(a.y, b.y) - r) - oy, y1 = CellY(Mathf.Max(a.y, b.y) + r) - oy;
            if (x1 < 0 || y1 < 0 || x0 >= w || y0 >= h) continue;
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(w - 1, x1); y1 = Math.Min(h - 1, y1);
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4000000) continue;
            var ab = b - a; float len = ab.sqrMagnitude;
            for (int y = y0; y <= y1; y++)
            {
                float cy = _fineOrigin.y + (y + oy + 0.5f) * FineCell;
                for (int x = x0; x <= x1; x++)
                {
                    int i = y * w + x;
                    if (grid[i]) continue;
                    var c = new Vector2(_fineOrigin.x + (x + ox + 0.5f) * FineCell, cy);
                    float t = len > 0f ? Mathf.Clamp01(Vector2.Dot(c - a, ab) / len) : 0f;
                    if ((a + ab * t - c).sqrMagnitude <= r2) grid[i] = true;
                }
            }
        }
        if (!s.Closed || n < 3) return;

        // Interior: scanline fill on cell centres.
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in pts) { if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y; }
        int ry0 = Math.Max(0, CellY(minY) - oy), ry1 = Math.Min(h - 1, CellY(maxY) - oy);
        var xs = new List<float>(8);
        for (int y = ry0; y <= ry1; y++)
        {
            float cy = _fineOrigin.y + (y + oy + 0.5f) * FineCell;
            xs.Clear();
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = pts[i]; var b = pts[j];
                if ((a.y > cy) != (b.y > cy))
                    xs.Add(a.x + (cy - a.y) * (b.x - a.x) / (b.y - a.y));
            }
            if (xs.Count < 2) continue;
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int xa = Math.Max(0, Mathf.CeilToInt((xs[k] - _fineOrigin.x) / FineCell - 0.5f) - ox);
                int xb = Math.Min(w - 1, Mathf.FloorToInt((xs[k + 1] - _fineOrigin.x) / FineCell - 0.5f) - ox);
                for (int x = xa; x <= xb; x++) grid[y * w + x] = true;
            }
        }
    }

    private static int CellX(float x) => Mathf.FloorToInt((x - _fineOrigin.x) / FineCell);
    private static int CellY(float y) => Mathf.FloorToInt((y - _fineOrigin.y) / FineCell);

    // ---- per-plan window ----------------------------------------------------

    private static bool TryStartFinePlan(Vector2 from, Vector2 to)
    {
        if (_fineStatic == null) return false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            int sx = CellX(from.x), sy = CellY(from.y), gx = CellX(to.x), gy = CellY(to.y);
            if (sx < 0 || sy < 0 || gx < 0 || gy < 0 || sx >= _fineW || gx >= _fineW || sy >= _fineH || gy >= _fineH)
                return false;   // off the outdoor map (a dungeon): the old path handles it

            float dist = Vector2.Distance(from, to);
            float margin = Mathf.Clamp(dist, FineMinMargin, FineMaxMargin);
            int m;
            int x0, y0, x1, y1;
            while (true)
            {
                m = Mathf.CeilToInt(margin / FineCell);
                x0 = Math.Max(0, Math.Min(sx, gx) - m); x1 = Math.Min(_fineW - 1, Math.Max(sx, gx) + m);
                y0 = Math.Max(0, Math.Min(sy, gy) - m); y1 = Math.Min(_fineH - 1, Math.Max(sy, gy) + m);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) <= FineMaxWindowCells || margin <= FineMinMargin) break;
                margin *= 0.8f;
            }
            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            if ((long)w * h > FineMaxWindowCells * 2L) return false;

            var grid = new bool[w * h];
            for (int y = 0; y < h; y++) Array.Copy(_fineStatic, (y + y0) * _fineW + x0, grid, y * w, w);

            int objs = StampWorldObjects(grid, w, h, x0, y0);
            int marks = StampBlockedCrossings(grid, w, h, x0, y0);

            int start = SnapFree(grid, w, h, sx - x0, sy - y0, c =>
                StartReachable(from, new Vector2(_fineOrigin.x + (c % w + x0 + 0.5f) * FineCell,
                                                 _fineOrigin.y + (c / w + y0 + 0.5f) * FineCell)));
            int goal = SnapFree(grid, w, h, gx - x0, gy - y0);
            if (start < 0 || goal < 0)
            {
                _log?.LogInfo($"[NAVIGATOR] Fine world route: no free ground near the " +
                              $"{(start < 0 ? "start" : "goal")}; using the road network");
                return false;
            }

            var job = new FinePlanJob
            {
                Token = ++_fineToken, From = from, To = to,
                Grid = grid, W = w, H = h, Ox = x0, Oy = y0, Start = start, Goal = goal,
            };
            _fineJob = job;
            ThreadPool.QueueUserWorkItem(_ => RunFinePlan(job));
            _log?.LogInfo($"[NAVIGATOR] Fine world route requested {from} -> {to}: window {w}x{h}, " +
                          $"{objs} objects and {marks} blocked crossings stamped, {sw.ElapsedMilliseconds}ms");
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] Fine world route setup failed: {ex.Message}");
            return false;
        }
    }

    /// Stamp the colliders of the world objects in the window — read from shape data, so chunks
    /// that are switched off count too. Cached per object until it moves or changes.
    private static int StampWorldObjects(bool[] grid, int w, int h, int ox, int oy)
    {
        float wx0 = _fineOrigin.x + ox * FineCell - 4f * TileSize, wx1 = _fineOrigin.x + (ox + w) * FineCell + 4f * TileSize;
        float wy0 = _fineOrigin.y + oy * FineCell - 4f * TileSize, wy1 = _fineOrigin.y + (oy + h) * FineCell + 4f * TileSize;
        var player = MainGame.me?.player;
        int playerLayer = PlayerCollisionLayer(player);
        var target = _longWalkTarget.Object;

        WorldObjectRegistry.Snapshot(_fineSnapshot);
        int count = 0;
        foreach (var wgo in _fineSnapshot)
        {
            if (wgo == null) continue;
            var p = wgo.pos;
            if (p.x < wx0 || p.x > wx1 || p.y < wy0 || p.y > wy1) continue;
            if (wgo == player || wgo.is_player || wgo == target) continue;

            if (!_wgoShapes.TryGetValue(wgo, out var cache) || cache.Pos != p || cache.ObjId != wgo.obj_id)
            {
                cache = new WgoShapeCache { Pos = p, ObjId = wgo.obj_id, Shapes = WgoShapes(wgo, playerLayer) };
                _wgoShapes[wgo] = cache;
            }
            if (cache.Shapes.Count == 0) continue;
            foreach (var s in cache.Shapes) StampShape(grid, w, h, ox, oy, s);
            count++;
        }
        _fineSnapshot.Clear();

        // Drop cache entries for objects that are gone.
        if (_wgoShapes.Count > 20000)
        {
            var dead = new List<WorldGameObject>();
            foreach (var k in _wgoShapes.Keys) if (k == null) dead.Add(k);
            foreach (var k in dead) _wgoShapes.Remove(k);
        }
        return count;
    }

    private static List<FineShape> WgoShapes(WorldGameObject wgo, int playerLayer)
    {
        var list = new List<FineShape>();
        try
        {
            var root = wgo.transform;
            var poly = new List<Vector2>(16);
            foreach (var col in wgo.GetComponentsInChildren<Collider2D>(true))
            {
                if (col == null || col.isTrigger || !col.enabled) continue;
                if (!WgoPartCounts(col.gameObject, root, playerLayer)) continue;
                var t = col.transform;
                poly.Clear();
                bool closed = true;
                switch (col)
                {
                    case BoxCollider2D b: AddBox(t, b.offset, b.size, poly); break;
                    case CapsuleCollider2D c: AddBox(t, c.offset, c.size, poly); break;
                    case CircleCollider2D c: AddCircle(t, c.offset, c.radius, poly); break;
                    case PolygonCollider2D pc:
                        for (int k = 0; k < pc.pathCount; k++)
                        {
                            var path = pc.GetPath(k);
                            var arr = new Vector2[path.Length];
                            for (int q = 0; q < path.Length; q++) arr[q] = t.TransformPoint(path[q] + pc.offset);
                            list.Add(new FineShape(arr, true));
                        }
                        continue;
                    case EdgeCollider2D e:
                        closed = false;
                        foreach (var v in e.points) poly.Add(t.TransformPoint(v + e.offset));
                        break;
                    default: continue;
                }
                if (poly.Count >= 2) list.Add(new FineShape(poly.ToArray(), closed));
            }
            foreach (var oc in wgo.GetComponentsInChildren<OptimizedCollider2D>(true))
            {
                if (oc == null || oc.is_trigger) continue;
                if (!WgoPartCounts(oc.gameObject, root, playerLayer)) continue;
                var t = oc.transform;
                poly.Clear();
                switch (oc)
                {
                    case OptimizedBoxCollider2D b: AddBox(t, b.offset, b.size, poly); break;
                    case OptimizedCircleCollider2D c: AddCircle(t, c.offset, c.radius, poly); break;
                    case OptimizedPolygonCollider2D pc:
                        if (pc.points == null) continue;
                        foreach (var v in pc.points) poly.Add(t.TransformPoint(v + pc.offset));
                        break;
                    default: continue;
                }
                if (poly.Count >= 2) list.Add(new FineShape(poly.ToArray(), true));
            }
        }
        catch { }
        return list;
    }

    /// A collider of a world object that the player bumps into, and that is part of the object as
    /// it is now (switched off only by chunk culling, not a hidden alternative state).
    private static bool WgoPartCounts(GameObject go, Transform root, int playerLayer)
    {
        int layer = go.layer;
        if (layer == CharactersLayer) return false;
        if (playerLayer >= 0 && Physics2D.GetIgnoreLayerCollision(playerLayer, layer)) return false;
        for (var t = go.transform; t != null; t = t.parent)
        {
            var g = t.gameObject;
            if (!g.activeSelf && g.GetComponent<ChunkedGameObject>() == null) return false;
            if (t == root) break;
        }
        return true;
    }

    private static int StampBlockedCrossings(bool[] grid, int w, int h, int ox, int oy)
    {
        int n = 0;
        foreach (var c in _blockedCrossings)
        {
            if (c.Nodes != null) continue;   // a wall: already in the static grid
            int x0 = Math.Max(0, CellX(c.Min.x) - ox), x1 = Math.Min(w - 1, CellX(c.Max.x) - ox);
            int y0 = Math.Max(0, CellY(c.Min.y) - oy), y1 = Math.Min(h - 1, CellY(c.Max.y) - oy);
            if (x1 < x0 || y1 < y0) continue;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++) grid[y * w + x] = true;
            n++;
        }
        return n;
    }

    /// Nearest free cell to (x,y). With <paramref name="accept"/>, only a cell it agrees to.
    private static int SnapFree(bool[] grid, int w, int h, int x, int y, Func<int, bool> accept = null)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return -1;
        if (!grid[y * w + x]) return y * w + x;
        var ring = new List<KeyValuePair<int, int>>();
        for (int r = 1; r <= FineSnapCells; r++)
        {
            ring.Clear();
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h || grid[yy * w + xx]) continue;
                    ring.Add(new KeyValuePair<int, int>(dx * dx + dy * dy, yy * w + xx));
                }
            ring.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var kv in ring)
                if (accept == null || accept(kv.Value)) return kv.Value;
        }
        return -1;
    }

    /// The player stands where physics put them, which may read as blocked on the grid when they
    /// are pressed against something. Only move the start to a cell the body can reach in a straight
    /// line — physics is trustworthy here, the start is always on screen. On 2026-11-19 the player
    /// stood at the blockade on the north route, the nearest free cell was on its FAR side, and the
    /// walk went 1.5 tiles through it into the swamp.
    private static bool StartReachable(Vector2 from, Vector2 cellCentre)
    {
        var d = cellCentre - from;
        float len = d.magnitude;
        if (len < 1f) return true;
        int steps = Mathf.CeilToInt(len / 4f);
        for (int s = 1; s <= steps; s++)
        {
            var p = from + d * (s / (float)steps);
            if (SolidAt(p) != null) return false;
        }
        return true;
    }

    // ---- search (worker thread: no Unity API here) --------------------------

    private static readonly int[] DirX = { 1, -1, 0, 0, 1, 1, -1, -1 };
    private static readonly int[] DirY = { 0, 0, 1, -1, 1, -1, 1, -1 };

    private static void RunFinePlan(FinePlanJob job)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            int w = job.W, h = job.H, n = w * h;
            var grid = job.Grid;
            var g = new int[n];
            for (int i = 0; i < n; i++) g[i] = int.MaxValue;
            var from = new byte[n];    // 0 = none, else direction index + 1 (step that arrived here)
            var closed = new bool[n];
            var heap = new MinHeap(1 << 16);
            int goal = job.Goal, gxx = goal % w, gyy = goal / w;

            int H(int i)
            {
                int dx = Math.Abs(i % w - gxx), dy = Math.Abs(i / w - gyy);
                return 10 * Math.Max(dx, dy) + 4 * Math.Min(dx, dy);
            }

            g[job.Start] = 0;
            heap.Push(H(job.Start), job.Start);
            int expanded = 0;
            bool found = false;
            while (heap.Count > 0)
            {
                int cur = heap.Pop();
                if (closed[cur]) continue;
                closed[cur] = true;
                expanded++;
                if (cur == goal) { found = true; break; }
                int cx = cur % w, cy = cur / w, gc = g[cur];
                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + DirX[d], ny = cy + DirY[d];
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (grid[ni] || closed[ni]) continue;
                    // No corner cutting: a diagonal step needs both straight neighbours free.
                    if (d >= 4 && (grid[cy * w + nx] || grid[ny * w + cx])) continue;
                    int ng = gc + (d >= 4 ? 14 : 10);
                    if (ng < g[ni])
                    {
                        g[ni] = ng;
                        from[ni] = (byte)(d + 1);
                        heap.Push(ng + H(ni), ni);
                    }
                }
            }
            job.Expanded = expanded;
            if (found)
            {
                var cells = new List<int>();
                int c = goal;
                while (c != job.Start)
                {
                    cells.Add(c);
                    int d = from[c] - 1;
                    if (d < 0) break;
                    c = (c / w - DirY[d]) * w + (c % w - DirX[d]);
                }
                cells.Add(job.Start);
                cells.Reverse();
                job.Cells = cells;
            }
            else
            {
                FindBarrier(job, closed);

                // The spot in front of the target sits in a pocket the grid cannot enter (between
                // a station and the wall, or a doorway alcove), but the walk can still get right
                // next to it: on 2026-11-19 the cellar design table was "no path" 1 tile short and
                // the road-network fallback wandered the cellar. Walk to the nearest reachable spot;
                // the usual final approach covers the last stretch.
                int f = job.Frontier;
                if (f >= 0 && closed[f])
                {
                    int dx = f % w - gxx, dy = f / w - gyy;
                    if (dx * dx + dy * dy <= FineNearEnoughCells * FineNearEnoughCells)
                    {
                        var cells = new List<int>();
                        int c = f;
                        while (c != job.Start)
                        {
                            cells.Add(c);
                            int d = from[c] - 1;
                            if (d < 0) break;
                            c = (c / w - DirY[d]) * w + (c % w - DirX[d]);
                        }
                        cells.Add(job.Start);
                        cells.Reverse();
                        job.Cells = cells;
                        job.ShortOfGoal = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
        }
        job.Ms = sw.ElapsedMilliseconds;
        job.Done = true;
    }

    /// Diagnostics for a failed search (worker thread). The A* above exhausted everything the start
    /// can reach (<paramref name="reach"/>). Flood the goal's own ground, then find the crossing
    /// between the two that passes through the fewest blocked cells — that is what closes the way,
    /// and the main thread names it.
    private static void FindBarrier(FinePlanJob job, bool[] reach)
    {
        int w = job.W, h = job.H, n = w * h;
        var grid = job.Grid;
        int gx = job.Goal % w, gy = job.Goal / w;

        int best = -1; long bestD = long.MaxValue;
        for (int i = 0; i < n; i++)
            if (reach[i])
            {
                long dx = i % w - gx, dy = i / w - gy, d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }
        job.Frontier = best;

        var goalSide = new bool[n];
        var q = new Queue<int>();
        goalSide[job.Goal] = true; q.Enqueue(job.Goal);
        while (q.Count > 0)
        {
            int c = q.Dequeue(), cx = c % w, cy = c / w;
            for (int d = 0; d < 4; d++)
            {
                int nx = cx + DirX[d], ny = cy + DirY[d];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int ni = ny * w + nx;
                if (goalSide[ni] || grid[ni]) continue;
                goalSide[ni] = true; q.Enqueue(ni);
            }
        }

        // 0-1 BFS: free cells cost 0, blocked cells 1, from the start's ground to the goal's.
        var dist = new int[n];
        var prev = new int[n];
        for (int i = 0; i < n; i++) { dist[i] = int.MaxValue; prev[i] = -1; }
        var dq = new LinkedList<int>();
        for (int i = 0; i < n; i++) if (reach[i]) { dist[i] = 0; dq.AddLast(i); }
        int hit = -1;
        while (dq.Count > 0)
        {
            int c = dq.First.Value; dq.RemoveFirst();
            if (goalSide[c]) { hit = c; break; }
            int cx = c % w, cy = c / w;
            for (int d = 0; d < 4; d++)
            {
                int nx = cx + DirX[d], ny = cy + DirY[d];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int ni = ny * w + nx;
                int nd = dist[c] + (grid[ni] ? 1 : 0);
                if (nd >= dist[ni]) continue;
                dist[ni] = nd; prev[ni] = c;
                if (grid[ni]) dq.AddLast(ni); else dq.AddFirst(ni);
            }
        }
        if (hit < 0) return;
        var barrier = new List<int>();
        for (int c = hit; c >= 0 && !reach[c]; c = prev[c])
            if (grid[c]) barrier.Add(c);
        barrier.Reverse();
        job.Barrier = barrier;
    }

    /// Main thread: say what the barrier cells are made of.
    private static void LogBarrier(FinePlanJob job)
    {
        _fineBarrierWgo = null;
        int w = job.W;
        Vector2 World(int c) => new Vector2(_fineOrigin.x + (c % w + job.Ox + 0.5f) * FineCell,
                                            _fineOrigin.y + (c / w + job.Oy + 0.5f) * FineCell);
        if (job.Frontier >= 0)
            _log?.LogInfo($"[NAVIGATOR] Fine world route: nearest reachable spot to the goal is {World(job.Frontier)}, " +
                          $"{Vector2.Distance(World(job.Frontier), job.To) / TileSize:F0} tiles short");
        if (job.Barrier == null || job.Barrier.Count == 0) return;

        var names = new List<string>();
        int walls = 0;
        foreach (int c in job.Barrier)
        {
            var p = World(c);
            int sx = c % w + job.Ox, sy = c / w + job.Oy;
            if (_fineStatic != null && _fineStatic[sy * _fineW + sx]) { walls++; continue; }
            foreach (var kv in _wgoShapes)
            {
                if (kv.Key == null || (kv.Value.Pos - p).sqrMagnitude > 12f * TileSize * 12f * TileSize) continue;
                bool covers = false;
                foreach (var s in kv.Value.Shapes)
                    if (ShapeCovers(s, p)) { covers = true; break; }
                if (!covers) continue;
                string label = $"{kv.Key.obj_id} at {kv.Value.Pos}";
                if (!names.Contains(label)) names.Add(label);
                if (_fineBarrierWgo == null) _fineBarrierWgo = kv.Key;
            }
        }
        _log?.LogInfo($"[NAVIGATOR] Fine world route: thinnest barrier is {job.Barrier.Count} blocked cells " +
                      $"from {World(job.Barrier[0])} to {World(job.Barrier[job.Barrier.Count - 1])}: " +
                      $"{walls} level geometry, objects: {(names.Count > 0 ? string.Join("; ", names.ToArray()) : "none")}");
    }

    private static bool ShapeCovers(FineShape s, Vector2 p)
    {
        var pts = s.Pts;
        int n = pts.Length;
        if (s.Closed && n >= 3)
        {
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = pts[i]; var b = pts[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            if (inside) return true;
        }
        float r2 = FineBodyRadius * FineBodyRadius;
        int segs = s.Closed ? n : n - 1;
        for (int k = 0; k < segs; k++)
        {
            var a = pts[k]; var b = pts[(k + 1) % n];
            var ab = b - a; float len = ab.sqrMagnitude;
            float t = len > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len) : 0f;
            if ((a + ab * t - p).sqrMagnitude <= r2) return true;
        }
        return false;
    }

    private sealed class MinHeap
    {
        private long[] _a;
        public int Count;
        public MinHeap(int cap) { _a = new long[cap]; }
        public void Push(int key, int val)
        {
            if (Count == _a.Length) Array.Resize(ref _a, _a.Length * 2);
            long v = ((long)key << 32) | (uint)val;
            int i = Count++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_a[p] <= v) break;
                _a[i] = _a[p]; i = p;
            }
            _a[i] = v;
        }
        public int Pop()
        {
            long top = _a[0];
            long last = _a[--Count];
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1;
                if (l >= Count) break;
                int r = l + 1;
                int m = r < Count && _a[r] < _a[l] ? r : l;
                if (_a[m] >= last) break;
                _a[i] = _a[m]; i = m;
            }
            if (Count > 0) _a[i] = last;
            return (int)(top & 0xFFFFFFFF);
        }
    }

    // ---- result (main thread) -----------------------------------------------

    private static void PumpFinePlan()
    {
        var job = _fineJob;
        if (job == null || !job.Done) return;
        _fineJob = null;
        if (job.Token != _fineToken) return;           // superseded
        _routePending = false;
        if (!_longWalkActive) return;                   // walk cancelled meanwhile

        try
        {
            if (job.Error != null || job.Cells == null || job.Cells.Count < 2)
            {
                _log?.LogInfo($"[NAVIGATOR] Fine world route: {(job.Error ?? "no path")} " +
                              $"({job.Expanded} cells searched, {job.Ms}ms); using the road network");
                try { LogBarrier(job); } catch (Exception ex) { _log?.LogWarning($"[NAVIGATOR] Barrier report failed: {ex.Message}"); }
                RequestGraph0Route(job.From, job.To, skipFine: true);
                _fineFoundNoPath = job.Error == null;
                return;
            }

            var pts = SimplifyFineRoute(job);
            // Keep the start cell as its own waypoint: the simplification checked lines from it,
            // not from where the player actually stands.
            pts.Insert(0, new Vector3(job.From.x, job.From.y, 0f));
            float len = PathLength(pts);
            if (job.ShortOfGoal)
                _log?.LogInfo($"[NAVIGATOR] Fine world route: the goal itself is boxed in; walking to the nearest " +
                              $"reachable spot, {Vector2.Distance(pts[pts.Count - 1], job.To) / TileSize:F1} tiles from it");
            _log?.LogInfo($"[NAVIGATOR] Fine world route: {job.Cells.Count} cells -> {pts.Count} waypoints, " +
                          $"{len / TileSize:F0} tiles for {Vector2.Distance(job.From, job.To) / TileSize:F0} straight " +
                          $"({job.Expanded} cells searched, {job.Ms}ms)");
            _routeReachesTarget = true;
            _finalPartial = false;
            StartNativePathWalk(pts, checkAgainstWorldMesh: false, fromWorldMesh: true);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] Fine world route hand-over failed: {ex.Message}");
            RequestGraph0Route(job.From, job.To, skipFine: true);
        }
    }

    /// Cell centres -> waypoints: keep a point only where a straight line from the last kept one
    /// would cross a blocked cell. Legs stay short enough for the lookahead to vet.
    private static List<Vector3> SimplifyFineRoute(FinePlanJob job)
    {
        var cells = job.Cells;
        int w = job.W;
        Vector3 Pt(int c) => new Vector3(_fineOrigin.x + (c % w + job.Ox + 0.5f) * FineCell,
                                         _fineOrigin.y + (c / w + job.Oy + 0.5f) * FineCell, 0f);
        var pts = new List<Vector3> { Pt(cells[0]) };
        const int MaxSkip = 40;   // ~6.5 tiles per leg at most
        int anchor = 0;
        while (anchor < cells.Count - 1)
        {
            int best = anchor + 1;
            for (int j = anchor + 2; j < cells.Count && j - anchor <= MaxSkip; j++)
            {
                if (LineClear(job, cells[anchor], cells[j])) best = j;
                else break;
            }
            pts.Add(Pt(cells[best]));
            anchor = best;
        }
        return pts;
    }

    private static bool LineClear(FinePlanJob job, int a, int b)
    {
        int w = job.W;
        float ax = a % w + 0.5f, ay = a / w + 0.5f, bx = b % w + 0.5f, by = b / w + 0.5f;
        float dx = bx - ax, dy = by - ay;
        int steps = Mathf.CeilToInt(Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) * 3f);
        for (int s = 1; s < steps; s++)
        {
            float t = s / (float)steps;
            int x = (int)(ax + dx * t), y = (int)(ay + dy * t);
            if (job.Grid[y * w + x]) return false;
        }
        return true;
    }
}
