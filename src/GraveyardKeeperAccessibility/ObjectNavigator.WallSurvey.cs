using System;
using System.Collections.Generic;
using UnityEngine;

namespace GraveyardKeeperAccessibility;

// ---- World wall survey ------------------------------------------------------
//
// Graph 0 is the NPC road network. It does not model the cliff faces, river banks and other level
// geometry the PLAYER collides with, so a road route regularly runs straight through a cliff. The
// lookahead only finds out on the spot (off-screen chunks are switched off and invisible to
// physics), and each blockage re-plan then just tries the next gap a few cells along: on
// 2026-11-18 the walk from home to the vineyard ran north along the steep_vert_L cliff, re-planning
// at y=2371, 3155 and 3915, while the real way is south through the kitchen garden.
//
// So, once per graph, read every wall collider in the world straight from its shape data — which
// works for switched-off chunks too — and tag the graph-0 cells it covers. Our own route queries
// keep off those cells; the game never uses tags and NPC seekers walk every tag, so NPCs are
// unaffected. If a query finds nothing with the walls excluded (a pass the marks closed by
// mistake), it is asked again without them.

internal static partial class ObjectNavigator
{
    private const int WallTag = 30;
    /// Mask for a route that keeps off both surveyed walls and blocked crossings.
    private const int RouteTagMaskWalls = ~((1 << BlockedTag) | (1 << WallTag));

    /// A cell is marked when its centre lies inside a wall or within this fraction of a node of
    /// its outline. Under half a node a thin wall could slip between two cells; much more and a
    /// narrow pass between two cliffs closes.
    private const float WallSurveyHalf = 0.45f;

    /// A single collider spanning more cells than this is skipped (logged) rather than rasterised.
    private const int WallSurveyMaxCells = 250000;

    /// The road route in flight was asked without the wall marks (after a query with them failed).
    private static bool _routeWallsRelaxed;
    private static Vector2 _routeQueryFrom, _routeQueryTo;

    private static Pathfinding.GridNode[] _wallSurveyFor;   // the graph-0 node array surveyed
    private static bool[] _wallCells;

    private static bool IsWallCell(int i) => _wallCells != null && i >= 0 && i < _wallCells.Length && _wallCells[i];

    /// <summary>Survey once per graph-0 instance. Cheap to call before every query.</summary>
    private static void EnsureWallSurvey()
    {
        var gg = Graph0Grid();
        if (gg?.nodes == null || gg.nodes.Length != gg.width * gg.depth) return;
        if (ReferenceEquals(gg.nodes, _wallSurveyFor)) return;
        _wallSurveyFor = gg.nodes;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var cells = new bool[gg.nodes.Length];
            int playerLayer = PlayerCollisionLayer(MainGame.me?.player);
            int used = 0, skippedHuge = 0;
            var poly = new List<Vector2>(64);
            var fineShapes = new List<FineShape>(8192);
            bool Rasterise(Pathfinding.GridGraph g, List<Vector2> pts, bool closed, bool[] into)
            {
                if (pts.Count >= 2) fineShapes.Add(new FineShape(pts.ToArray(), closed));
                return RasteriseGraph0(g, pts, closed, into);
            }

            foreach (var col in Resources.FindObjectsOfTypeAll<Collider2D>())
            {
                if (col == null || col.isTrigger || !col.enabled) continue;
                if (!IsSurveyWall(col.gameObject, playerLayer)) continue;
                var t = col.transform;
                bool closed = true;
                poly.Clear();
                switch (col)
                {
                    case BoxCollider2D b: AddBox(t, b.offset, b.size, poly); break;
                    case CapsuleCollider2D c: AddBox(t, c.offset, c.size, poly); break;
                    case CircleCollider2D c: AddCircle(t, c.offset, c.radius, poly); break;
                    case PolygonCollider2D p:
                        for (int k = 0; k < p.pathCount; k++)
                        {
                            poly.Clear();
                            foreach (var v in p.GetPath(k)) poly.Add(t.TransformPoint(v + p.offset));
                            if (Rasterise(gg, poly, true, cells)) used++; else skippedHuge++;
                        }
                        continue;
                    case EdgeCollider2D e:
                        closed = false;
                        foreach (var v in e.points) poly.Add(t.TransformPoint(v + e.offset));
                        break;
                    default: continue;
                }
                if (Rasterise(gg, poly, closed, cells)) used++; else skippedHuge++;
            }

            // Colliders the game only builds when their chunk first switches on.
            foreach (var oc in Resources.FindObjectsOfTypeAll<OptimizedCollider2D>())
            {
                if (oc == null || oc.is_trigger) continue;
                if (!IsSurveyWall(oc.gameObject, playerLayer)) continue;
                var t = oc.transform;
                poly.Clear();
                switch (oc)
                {
                    case OptimizedBoxCollider2D b: AddBox(t, b.offset, b.size, poly); break;
                    case OptimizedCircleCollider2D c: AddCircle(t, c.offset, c.radius, poly); break;
                    case OptimizedPolygonCollider2D p:
                        if (p.points == null) continue;
                        foreach (var v in p.points) poly.Add(t.TransformPoint(v + p.offset));
                        break;
                    default: continue;
                }
                if (Rasterise(gg, poly, true, cells)) used++; else skippedHuge++;
            }

            int marked = 0;
            var toTag = new List<Pathfinding.GraphNode>();
            for (int i = 0; i < cells.Length; i++)
            {
                if (!cells[i]) continue;
                var nd = gg.nodes[i];
                if (nd == null || !nd.Walkable) { cells[i] = false; continue; }
                marked++;
                if (nd.Tag == 0) toTag.Add(nd);
            }
            _wallCells = cells;

            var astar = AstarPath.active;
            astar.AddWorkItem(new AstarPath.AstarWorkItem(force =>
            {
                foreach (var nd in toTag) if (nd.Tag == 0) nd.Tag = WallTag;
                return true;
            }));
            astar.FlushWorkItems();

            _log?.LogInfo($"[NAVIGATOR] Wall survey: {used} wall shapes, {marked} walkable road-network cells " +
                          $"marked as wall ({skippedHuge} too big to survey), {sw.ElapsedMilliseconds}ms");

            BuildFineStatic(gg, fineShapes);
        }
        catch (Exception ex)
        {
            _wallCells = null;
            _log?.LogWarning($"[NAVIGATOR] Wall survey failed: {ex.Message}");
        }
    }

    /// Level geometry the player collides with, in the world (not a prefab asset), and either
    /// switched on or switched off only by chunk culling — never something the game turned off
    /// for good, such as a cleared obstacle.
    private static bool IsSurveyWall(GameObject go, int playerLayer)
    {
        if (go == null || !go.scene.IsValid()) return false;
        int layer = go.layer;
        if (layer == CharactersLayer) return false;
        if (playerLayer >= 0 && Physics2D.GetIgnoreLayerCollision(playerLayer, layer)) return false;
        for (var t = go.transform; t != null; t = t.parent)
        {
            var g = t.gameObject;
            if (g.GetComponent<WorldGameObject>() != null) return false;   // props and buildings: not walls
            if (!g.activeSelf && g.GetComponent<ChunkedGameObject>() == null) return false;
        }
        return true;
    }

    private static void AddBox(Transform t, Vector2 offset, Vector2 size, List<Vector2> poly)
    {
        var h = size * 0.5f;
        poly.Add(t.TransformPoint(offset + new Vector2(-h.x, -h.y)));
        poly.Add(t.TransformPoint(offset + new Vector2(h.x, -h.y)));
        poly.Add(t.TransformPoint(offset + new Vector2(h.x, h.y)));
        poly.Add(t.TransformPoint(offset + new Vector2(-h.x, h.y)));
    }

    private static void AddCircle(Transform t, Vector2 offset, float radius, List<Vector2> poly)
    {
        for (int k = 0; k < 8; k++)
        {
            float a = k * Mathf.PI / 4f;
            poly.Add(t.TransformPoint(offset + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius));
        }
    }

    /// Mark the cells under one shape. False when it is too big to survey.
    private static bool RasteriseGraph0(Pathfinding.GridGraph gg, List<Vector2> pts, bool closed, bool[] cells)
    {
        if (pts.Count < 2) return true;
        float half = gg.nodeSize * WallSurveyHalf;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in pts)
        {
            if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
        }
        minX -= half; minY -= half; maxX += half; maxY += half;

        // Grid-space index range of the box (the grid may be rotated, so take all four corners).
        int gx0 = int.MaxValue, gz0 = int.MaxValue, gx1 = int.MinValue, gz1 = int.MinValue;
        void Corner(float x, float y)
        {
            var v = gg.inverseMatrix.MultiplyPoint3x4(new Vector3(x, y, 0f));
            int ix = Mathf.FloorToInt(v.x), iz = Mathf.FloorToInt(v.z);
            if (ix < gx0) gx0 = ix; if (ix > gx1) gx1 = ix;
            if (iz < gz0) gz0 = iz; if (iz > gz1) gz1 = iz;
        }
        Corner(minX, minY); Corner(maxX, minY); Corner(minX, maxY); Corner(maxX, maxY);
        gx0 = Mathf.Max(0, gx0 - 1); gz0 = Mathf.Max(0, gz0 - 1);
        gx1 = Mathf.Min(gg.width - 1, gx1 + 1); gz1 = Mathf.Min(gg.depth - 1, gz1 + 1);
        if (gx1 < gx0 || gz1 < gz0) return true;   // outside the road network
        if ((long)(gx1 - gx0 + 1) * (gz1 - gz0 + 1) > WallSurveyMaxCells) return false;

        float halfSq = half * half;
        for (int z = gz0; z <= gz1; z++)
            for (int x = gx0; x <= gx1; x++)
            {
                int i = z * gg.width + x;
                if (cells[i]) continue;
                var nd = gg.nodes[i];
                if (nd == null) continue;
                var c3 = (Vector3)nd.position;
                var c = new Vector2(c3.x, c3.y);
                if (c.x < minX || c.x > maxX || c.y < minY || c.y > maxY) continue;
                if ((closed && PointInPolygon(c, pts)) || NearOutline(c, pts, closed, halfSq))
                    cells[i] = true;
            }
        return true;
    }

    private static bool PointInPolygon(Vector2 p, List<Vector2> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            if ((a.y > p.y) != (b.y > p.y) &&
                p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                inside = !inside;
        }
        return inside;
    }

    private static bool NearOutline(Vector2 p, List<Vector2> pts, bool closed, float distSq)
    {
        int n = pts.Count;
        int segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % n];
            var ab = b - a;
            float len = ab.sqrMagnitude;
            float t = len > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len) : 0f;
            if ((a + ab * t - p).sqrMagnitude <= distSq) return true;
        }
        return false;
    }
}
