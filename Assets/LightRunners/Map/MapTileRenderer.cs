using System;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Map
{
    /// <summary>
    /// Composites a 3×3 tile grid into one RGB texture and draws a separate RGBA overlay
    /// (player dot + trail polylines, Bresenham thickness 2). Spec §10.4. Pixel space is
    /// slippy-map mercator relative to the center tile — independent of CoordinateConverter
    /// (spec §5.2 pitfall: do not unify).
    /// </summary>
    public class MapTileRenderer
    {
        private const int Grid = 3;
        public const int CompositeSize = Grid * OSMTileProvider.TileSize; // 768

        private readonly Texture2D _composite;
        private readonly Texture2D _overlay;
        private readonly Color32[] _overlayClear;

        private int _zoom;
        private int _centerTileX, _centerTileY;

        public Texture2D Composite => _composite;
        public Texture2D Overlay => _overlay;
        public int Zoom => _zoom;
        public int CenterTileX => _centerTileX;
        public int CenterTileY => _centerTileY;

        public MapTileRenderer()
        {
            _composite = new Texture2D(CompositeSize, CompositeSize, TextureFormat.RGB24, false)
            { name = "OSMComposite", wrapMode = TextureWrapMode.Clamp };
            _overlay = new Texture2D(CompositeSize, CompositeSize, TextureFormat.RGBA32, false)
            { name = "OSMOverlay", wrapMode = TextureWrapMode.Clamp };
            _overlayClear = new Color32[CompositeSize * CompositeSize];
            ClearOverlay();
            ApplyOverlay();
        }

        /// <summary>Set the 3×3 grid center. Returns true if the center tile changed (tiles need refetch).</summary>
        public bool SetCenter(double lat, double lon, int zoom)
        {
            OSMTileProvider.LatLonToTile(lat, lon, zoom, out double tx, out double ty);
            int cx = (int)Math.Floor(tx);
            int cy = (int)Math.Floor(ty);
            bool changed = cx != _centerTileX || cy != _centerTileY || zoom != _zoom;
            _centerTileX = cx;
            _centerTileY = cy;
            _zoom = zoom;
            return changed;
        }

        /// <summary>Grid tile coordinates for fetching: (dx, dy) in [-1, 1] around the center tile.</summary>
        public void GetTileCoords(int dx, int dy, out int x, out int y)
        {
            x = _centerTileX + dx;
            y = _centerTileY + dy;
        }

        /// <summary>Blit one fetched tile into its grid slot. Composite Y is flipped for screen space.</summary>
        public void BlitTile(int dx, int dy, Texture2D tile)
        {
            if (tile == null) return;
            int ts = OSMTileProvider.TileSize;
            var pixels = tile.GetPixels32();

            int gridCol = dx + 1;           // 0..2 left→right
            int gridRow = dy + 1;           // 0..2 top→bottom (tile y grows south)
            int destX0 = gridCol * ts;
            int destY0 = (Grid - 1 - gridRow) * ts; // flip: texture Y grows up

            // Tile pixels come in bottom-up already (GetPixels32 origin bottom-left), and the
            // tile image itself is top-down geographic — the double flip cancels, so a direct
            // row copy lands correctly.
            var dest = new Color32[ts * ts];
            for (int row = 0; row < ts; row++)
                Array.Copy(pixels, row * ts, dest, row * ts, ts);

            _composite.SetPixels32(destX0, destY0, ts, ts, dest);
            _composite.Apply(false);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Geo → composite pixel (spec §10.4)
        // ─────────────────────────────────────────────────────────────────────
        public Vector2Int GeoToPixel(double lat, double lon)
        {
            OSMTileProvider.LatLonToTile(lat, lon, _zoom, out double tx, out double ty);
            // Offset in tiles from the grid's top-left tile.
            double ox = tx - (_centerTileX - 1);
            double oy = ty - (_centerTileY - 1);
            int px = (int)(ox * OSMTileProvider.TileSize);
            int py = (int)(oy * OSMTileProvider.TileSize);
            // Texture space: origin bottom-left, tile y grows south → flip.
            return new Vector2Int(px, CompositeSize - 1 - py);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Overlay drawing
        // ─────────────────────────────────────────────────────────────────────
        public void ClearOverlay() => _overlay.SetPixels32(_overlayClear);

        public void ApplyOverlay() => _overlay.Apply(false);

        /// <summary>Filled-circle player dot.</summary>
        public void DrawPlayerDot(Vector2Int p, Color color, int radius = 7)
        {
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                    if (dx * dx + dy * dy <= radius * radius)
                        SetPixelSafe(p.x + dx, p.y + dy, color);
        }

        /// <summary>Bresenham polyline, thickness 2 (spec §10.4). Breaks on discontinuity points (spec §20).</summary>
        public void DrawPolyline(System.Collections.Generic.IReadOnlyList<TrailPoint> points, Color color)
        {
            for (int i = 1; i < points.Count; i++)
            {
                if (points[i].isSegmentStart) continue;
                Vector2Int a = GeoToPixel(points[i - 1].position.latitude, points[i - 1].position.longitude);
                Vector2Int b = GeoToPixel(points[i].position.latitude, points[i].position.longitude);
                DrawLine(a, b, color);
            }
        }

        private void DrawLine(Vector2Int a, Vector2Int b, Color color)
        {
            int x0 = a.x, y0 = a.y, x1 = b.x, y1 = b.y;
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;

            // Hard cap: a segment fully off-grid can't loop forever.
            int guard = CompositeSize * 4;
            while (guard-- > 0)
            {
                // Thickness 4: plus-shaped around the line point so the trail reads clearly
                // against the dark CARTO basemap.
                SetPixelSafe(x0,     y0,     color);
                SetPixelSafe(x0 + 1, y0,     color);
                SetPixelSafe(x0 - 1, y0,     color);
                SetPixelSafe(x0,     y0 + 1, color);
                SetPixelSafe(x0,     y0 - 1, color);

                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        private void SetPixelSafe(int x, int y, Color c)
        {
            if (x < 0 || y < 0 || x >= CompositeSize || y >= CompositeSize) return;
            _overlay.SetPixel(x, y, c);
        }
    }
}
