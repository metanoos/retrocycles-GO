using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using LightRunners.Core;

namespace LightRunners.Map
{
    /// <summary>
    /// Fetches OSM raster tiles (spec §10.3) with mandatory policy compliance, enforced by
    /// config: a real User-Agent, ≤ osmMaxConcurrentRequests in flight, ≥ osmTileRequestInterval
    /// between request starts. Two-level cache: in-memory LRU (osmTileCacheSize) + on-disk PNG
    /// at persistentDataPath/osm_tiles.
    ///
    /// Production note (spec §10.3): the free openstreetmap.org endpoint is for dev only;
    /// self-host tiles for scale.
    /// </summary>
    public class OSMTileProvider : MonoBehaviour
    {
        public const int TileSize = 256;
        // Dark "neon-friendly" basemap: CARTO Dark Matter (free, no API key, OSM-licensed).
        // Spec §10.3 still applies for production scale — self-host or move to a paid provider.
        private const string UrlTemplate = "https://cartodb-basemaps-a.global.ssl.fastly.net/dark_all/{0}/{1}/{2}.png";

        private sealed class TileRequest
        {
            public int z, x, y;
            public Action<Texture2D> callback;
            public string Key => $"{z}/{x}/{y}";
        }

        // LRU: dictionary + linked list of keys, most-recent at the front.
        private readonly Dictionary<string, Texture2D> _memoryCache = new Dictionary<string, Texture2D>();
        private readonly LinkedList<string> _lru = new LinkedList<string>();

        private readonly Queue<TileRequest> _queue = new Queue<TileRequest>();
        private readonly HashSet<string> _pending = new HashSet<string>();
        private int _inFlight;
        private float _lastRequestStart = -999f;

        private string DiskDir => Path.Combine(Application.persistentDataPath, "osm_tiles");

        // ─────────────────────────────────────────────────────────────────────
        // Slippy-map mercator math (spec §5.2 — independent of CoordinateConverter)
        // ─────────────────────────────────────────────────────────────────────
        public static void LatLonToTile(double lat, double lon, int zoom, out double x, out double y)
        {
            double latRad = lat * Math.PI / 180.0;
            double n = Math.Pow(2.0, zoom);
            x = (lon + 180.0) / 360.0 * n;
            y = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n;
        }

        public static void TileToLatLon(double x, double y, int zoom, out double lat, out double lon)
        {
            double n = Math.Pow(2.0, zoom);
            lon = x / n * 360.0 - 180.0;
            double latRad = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / n)));
            lat = latRad * 180.0 / Math.PI;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Get a tile: memory → disk → network queue. The callback fires exactly once, with
        /// null on hard failure. Cached hits fire synchronously.
        /// </summary>
        public void GetTile(int z, int x, int y, Action<Texture2D> callback)
        {
            string key = $"{z}/{x}/{y}";

            if (_memoryCache.TryGetValue(key, out var tex) && tex != null)
            {
                Touch(key);
                callback?.Invoke(tex);
                return;
            }

            // Disk cache (synchronous read — a 256² PNG decode is ~1 ms).
            string diskPath = Path.Combine(DiskDir, $"{z}_{x}_{y}.png");
            if (File.Exists(diskPath))
            {
                try
                {
                    var bytes = File.ReadAllBytes(diskPath);
                    var t = new Texture2D(TileSize, TileSize, TextureFormat.RGB24, false);
                    if (t.LoadImage(bytes))
                    {
                        Store(key, t);
                        callback?.Invoke(t);
                        return;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[OSMTileProvider] disk cache read failed for {key}: {e.Message}");
                }
            }

            if (_pending.Contains(key))
            {
                // Already queued/in flight: chain the callback via a wrapper request that
                // resolves from the cache when the fetch lands.
                StartCoroutine(CoWaitForCache(key, callback));
                return;
            }

            _pending.Add(key);
            _queue.Enqueue(new TileRequest { z = z, x = x, y = y, callback = callback });
        }

        private IEnumerator CoWaitForCache(string key, Action<Texture2D> callback)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (_memoryCache.TryGetValue(key, out var tex)) { callback?.Invoke(tex); yield break; }
                if (!_pending.Contains(key)) { callback?.Invoke(null); yield break; } // failed
                yield return null;
            }
            callback?.Invoke(null);
        }

        private void Update()
        {
            GameConfig cfg = GameConfig.Active;
            if (_queue.Count == 0) return;
            if (_inFlight >= cfg.osmMaxConcurrentRequests) return;
            if (Time.realtimeSinceStartup - _lastRequestStart < cfg.osmTileRequestInterval) return;

            var req = _queue.Dequeue();
            _lastRequestStart = Time.realtimeSinceStartup;
            _inFlight++;
            StartCoroutine(CoFetch(req));
        }

        private IEnumerator CoFetch(TileRequest req)
        {
            string url = string.Format(UrlTemplate, req.z, req.x, req.y);
            using (var www = UnityWebRequestTexture.GetTexture(url, nonReadable: false))
            {
                www.SetRequestHeader("User-Agent", GameConfig.Active.osmTileUserAgent);
                yield return www.SendWebRequest();

                _inFlight--;
                string key = req.Key;
                _pending.Remove(key);

                if (www.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[OSMTileProvider] tile {key} failed: {www.error}");
                    req.callback?.Invoke(null);
                    yield break;
                }

                var tex = DownloadHandlerTexture.GetContent(www);
                Store(key, tex);

                // Disk cache, best-effort.
                try
                {
                    Directory.CreateDirectory(DiskDir);
                    File.WriteAllBytes(Path.Combine(DiskDir, $"{req.z}_{req.x}_{req.y}.png"), www.downloadHandler.data);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[OSMTileProvider] disk cache write failed: {e.Message}");
                }

                req.callback?.Invoke(tex);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // LRU
        // ─────────────────────────────────────────────────────────────────────
        private void Store(string key, Texture2D tex)
        {
            if (_memoryCache.ContainsKey(key)) { _memoryCache[key] = tex; Touch(key); return; }
            _memoryCache[key] = tex;
            _lru.AddFirst(key);

            int cap = Mathf.Max(8, GameConfig.Active.osmTileCacheSize);
            while (_lru.Count > cap)
            {
                string evict = _lru.Last.Value;
                _lru.RemoveLast();
                if (_memoryCache.TryGetValue(evict, out var old) && old != null) Destroy(old);
                _memoryCache.Remove(evict);
            }
        }

        private void Touch(string key)
        {
            var node = _lru.Find(key);
            if (node != null)
            {
                _lru.Remove(node);
                _lru.AddFirst(key);
            }
        }
    }
}
