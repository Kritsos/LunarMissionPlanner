using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Reads the preprocessed NASA LOLA LDEM_64 tiled RAW dataset from
/// Assets/StreamingAssets/LOLA_LDEM64.
///
/// Tiles store the original signed 16-bit DN values. Elevation is DN * 0.5 m.
/// The reader loads tiles on demand and keeps a small least-recently-used cache.
/// Intended for Unity Editor / Windows desktop builds (ordinary file I/O).
/// </summary>
public sealed class LunarDEMReader : MonoBehaviour
{
    [Header("StreamingAssets dataset")]
    [SerializeField] private string dataFolderName = "LOLA_LDEM64";
    [SerializeField] private string manifestFileName = "manifest.json";

    [Header("Tile cache")]
    [Min(1)] [SerializeField] private int maxCachedTiles = 8;

    public bool IsInitialized { get; private set; }
    public double ReferenceRadiusMeters => _manifest != null
        ? _manifest.reference.reference_sphere_radius_m
        : 1737400.0;
    public int CachedTileCount => _cache.Count;
    public string DatasetDirectory => Path.Combine(Application.streamingAssetsPath, dataFolderName);

    private ManifestRoot _manifest;
    private TileInfo[,] _tileIndex;
    private readonly Dictionary<int, CachedTile> _cache = new Dictionary<int, CachedTile>();
    private readonly LinkedList<int> _lru = new LinkedList<int>();

    [Serializable]
    private sealed class ManifestRoot
    {
        public string format;
        public ManifestReference reference;
        public ManifestSourceGrid source_grid;
        public ManifestElevationEncoding elevation_encoding;
        public ManifestTileLayout tile_layout;
        public TileInfo[] tiles;
    }

    [Serializable]
    private sealed class ManifestReference
    {
        public double reference_sphere_radius_m;
        public string latitude_type;
        public string longitude_direction;
    }

    [Serializable]
    private sealed class ManifestSourceGrid
    {
        public int rows;
        public int columns;
        public double pixels_per_degree;
        public double line_projection_offset_zero_based;
        public double sample_projection_offset_zero_based;
    }

    [Serializable]
    private sealed class ManifestElevationEncoding
    {
        public double scaling_factor_m_per_dn;
        public double offset_m;
    }

    [Serializable]
    private sealed class ManifestTileLayout
    {
        public int core_tile_size_samples;
        public int halo_samples;
        public int tile_rows;
        public int tile_columns;
        public int tile_count;
    }

    [Serializable]
    private sealed class TileInfo
    {
        public int tile_row;
        public int tile_col;
        public string file;
        public StoredWithHalo stored_with_halo;
        public long file_size_bytes;
    }

    [Serializable]
    private sealed class StoredWithHalo
    {
        public int row_start;
        public int column_start_wrapped;
        public int rows;
        public int columns;
        public int halo_samples;
    }

    private sealed class CachedTile
    {
        public short[] Samples;
        public int Rows;
        public int Columns;
        public LinkedListNode<int> LruNode;
    }

    private void Awake()
    {
        if (!TryInitialize(out string error))
            Debug.LogError($"[{nameof(LunarDEMReader)}] Initialization failed: {error}", this);
    }

    private void OnValidate()
    {
        maxCachedTiles = Mathf.Max(1, maxCachedTiles);
    }

    private void OnDestroy()
    {
        _cache.Clear();
        _lru.Clear();
    }

    /// <summary>Load and validate manifest.json. Tile contents remain unloaded until queried.</summary>
    public bool TryInitialize(out string error)
    {
        error = null;
        if (IsInitialized)
            return true;

        try
        {
            if (!BitConverter.IsLittleEndian)
                throw new PlatformNotSupportedException(
                    "This reader currently expects a little-endian runtime (Windows PCs are little-endian).");

            string manifestPath = Path.Combine(DatasetDirectory, manifestFileName);
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException(
                    $"Manifest not found: {manifestPath}. Put manifest.json and the .raw tiles in " +
                    $"Assets/StreamingAssets/{dataFolderName}/.", manifestPath);

            string json = File.ReadAllText(manifestPath);
            _manifest = JsonUtility.FromJson<ManifestRoot>(json);
            ValidateManifest(_manifest);

            _tileIndex = new TileInfo[
                _manifest.tile_layout.tile_rows,
                _manifest.tile_layout.tile_columns];

            foreach (TileInfo tile in _manifest.tiles)
            {
                if (tile.tile_row < 0 || tile.tile_row >= _tileIndex.GetLength(0) ||
                    tile.tile_col < 0 || tile.tile_col >= _tileIndex.GetLength(1))
                    throw new InvalidDataException("Manifest contains a tile with an out-of-range index.");

                if (_tileIndex[tile.tile_row, tile.tile_col] != null)
                    throw new InvalidDataException(
                        $"Manifest contains duplicate tile ({tile.tile_row}, {tile.tile_col}).");

                _tileIndex[tile.tile_row, tile.tile_col] = tile;
            }

            for (int row = 0; row < _tileIndex.GetLength(0); row++)
            for (int col = 0; col < _tileIndex.GetLength(1); col++)
                if (_tileIndex[row, col] == null)
                    throw new InvalidDataException($"Manifest is missing tile ({row}, {col}).");

            IsInitialized = true;
            Debug.Log(
                $"[{nameof(LunarDEMReader)}] Loaded {_manifest.format}: " +
                $"{_manifest.source_grid.rows} x {_manifest.source_grid.columns}, " +
                $"{_manifest.tiles.Length} tiles. Tiles load on demand.", this);
            return true;
        }
        catch (Exception ex)
        {
            _manifest = null;
            _tileIndex = null;
            IsInitialized = false;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Bilinearly samples LOLA at planetocentric latitude and east-positive longitude.
    /// Longitude may be supplied in either -180..180 or 0..360 form.
    /// </summary>
    public bool TryGetElevation(
        double latitudeDeg,
        double longitudeEastDeg,
        out LolaElevationSample sample,
        out string error)
    {
        sample = default;
        error = null;

        if (!TryEnsureInitialized(out error))
            return false;

        if (double.IsNaN(latitudeDeg) || double.IsInfinity(latitudeDeg) ||
            latitudeDeg < -90.0 || latitudeDeg > 90.0)
        {
            error = "Latitude must be a finite value between -90 and +90 degrees.";
            return false;
        }

        if (double.IsNaN(longitudeEastDeg) || double.IsInfinity(longitudeEastDeg))
        {
            error = "Longitude must be a finite number.";
            return false;
        }

        try
        {
            double longitudeNormalized = LunarCoordinates.NormalizeLongitudeEast(longitudeEastDeg);
            double row = _manifest.source_grid.line_projection_offset_zero_based -
                         _manifest.source_grid.pixels_per_degree * latitudeDeg;
            double column = _manifest.source_grid.sample_projection_offset_zero_based +
                            _manifest.source_grid.pixels_per_degree *
                            (longitudeNormalized - 180.0);
            column = PositiveModulo(column, _manifest.source_grid.columns);

            // At exactly +/-90 degrees the geographic pole lies on the grid boundary.
            // Clamp to the nearest represented pixel centre.
            row = Math.Max(0.0, Math.Min(_manifest.source_grid.rows - 1.0, row));

            int row0 = (int)Math.Floor(row);
            int row1 = Math.Min(row0 + 1, _manifest.source_grid.rows - 1);
            int col0 = PositiveModulo((int)Math.Floor(column), _manifest.source_grid.columns);
            int col1 = (col0 + 1) % _manifest.source_grid.columns;

            double fr = row - row0;
            double fc = column - Math.Floor(column);

            int tileRow = row0 / _manifest.tile_layout.core_tile_size_samples;
            int tileCol = col0 / _manifest.tile_layout.core_tile_size_samples;
            TileInfo tileInfo = _tileIndex[tileRow, tileCol];
            CachedTile tile = GetTile(tileInfo);

            double dn00 = ReadSample(tile, tileInfo, row0, col0);
            double dn01 = ReadSample(tile, tileInfo, row0, col1);
            double dn10 = ReadSample(tile, tileInfo, row1, col0);
            double dn11 = ReadSample(tile, tileInfo, row1, col1);

            double dn =
                (1.0 - fr) * ((1.0 - fc) * dn00 + fc * dn01) +
                fr * ((1.0 - fc) * dn10 + fc * dn11);

            double elevation = dn * _manifest.elevation_encoding.scaling_factor_m_per_dn;
            double radius = _manifest.reference.reference_sphere_radius_m + elevation;

            sample = new LolaElevationSample(
                latitudeDeg,
                longitudeNormalized,
                row,
                column,
                dn,
                elevation,
                radius);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public double GetElevationMeters(double latitudeDeg, double longitudeEastDeg)
    {
        if (TryGetElevation(latitudeDeg, longitudeEastDeg, out LolaElevationSample sample, out string error))
            return sample.elevationMeters;
        throw new InvalidOperationException($"LOLA elevation query failed: {error}");
    }

    /// <summary>Returns the zero-based fractional source-grid row/column for diagnostics.</summary>
    public bool TryGetFractionalGridCoordinates(
        double latitudeDeg,
        double longitudeEastDeg,
        out double row,
        out double column,
        out string error)
    {
        row = 0.0;
        column = 0.0;
        error = null;
        if (!TryEnsureInitialized(out error))
            return false;
        if (double.IsNaN(latitudeDeg) || double.IsInfinity(latitudeDeg) || latitudeDeg < -90 || latitudeDeg > 90)
        {
            error = "Latitude must be between -90 and +90 degrees.";
            return false;
        }
        if (double.IsNaN(longitudeEastDeg) || double.IsInfinity(longitudeEastDeg))
        {
            error = "Longitude must be finite.";
            return false;
        }

        double lon = LunarCoordinates.NormalizeLongitudeEast(longitudeEastDeg);
        row = _manifest.source_grid.line_projection_offset_zero_based -
              _manifest.source_grid.pixels_per_degree * latitudeDeg;
        row = Math.Max(0.0, Math.Min(_manifest.source_grid.rows - 1.0, row));
        column = PositiveModulo(
            _manifest.source_grid.sample_projection_offset_zero_based +
            _manifest.source_grid.pixels_per_degree * (lon - 180.0),
            _manifest.source_grid.columns);
        return true;
    }

    /// <summary>Checks the five project regression landmarks against the uploaded CSV.</summary>
    [ContextMenu("Validate Five LOLA Regression Landmarks")]
    public void ValidateRegressionLandmarks()
    {
        // Expected values are the values in landmark_samples.csv, rounded to 0.001 m.
        ValidateLandmark("Mare Nectaris", -15.19, 34.60, -2770.656);
        ValidateLandmark("Kepler", 8.12, -38.01, -2884.316);
        ValidateLandmark("Copernicus", 9.62, -20.08, -3338.774);
        ValidateLandmark("Tycho", -43.30, -11.22, -2839.120);
        ValidateLandmark("Aristarchus", 23.73, -47.49, -3175.025);
    }

    private void ValidateLandmark(string name, double lat, double lon, double expectedHeight)
    {
        if (!TryGetElevation(lat, lon, out LolaElevationSample sample, out string error))
        {
            Debug.LogError($"LOLA test '{name}' failed: {error}", this);
            return;
        }

        double difference = sample.elevationMeters - expectedHeight;
        bool pass = Math.Abs(difference) <= 0.0011;
        Debug.Log(
            $"[{(pass ? "PASS" : "FAIL")}] {name}: " +
            $"height={sample.elevationMeters:F3} m, expected={expectedHeight:F3} m, " +
            $"difference={difference:+0.0000;-0.0000;0.0000} m; " +
            $"grid=({sample.fractionalRow:F3}, {sample.fractionalColumn:F3})",
            this);
    }

    private bool TryEnsureInitialized(out string error)
    {
        if (IsInitialized)
        {
            error = null;
            return true;
        }
        return TryInitialize(out error);
    }

    private void ValidateManifest(ManifestRoot manifest)
    {
        if (manifest == null)
            throw new InvalidDataException("manifest.json could not be parsed.");
        if (manifest.format != "LOLA_LDEM64_TILED_RAW_V1")
            throw new InvalidDataException($"Unexpected manifest format: {manifest.format ?? "<null>"}.");
        if (manifest.reference == null || manifest.source_grid == null ||
            manifest.elevation_encoding == null || manifest.tile_layout == null || manifest.tiles == null)
            throw new InvalidDataException("Manifest is missing one or more required sections.");
        if (manifest.source_grid.rows <= 0 || manifest.source_grid.columns <= 0 ||
            manifest.source_grid.pixels_per_degree <= 0.0)
            throw new InvalidDataException("Manifest source-grid dimensions or resolution are invalid.");
        if (manifest.reference.reference_sphere_radius_m <= 0.0 ||
            manifest.elevation_encoding.scaling_factor_m_per_dn <= 0.0)
            throw new InvalidDataException("Manifest radius or elevation scale is invalid.");
        if (manifest.tile_layout.core_tile_size_samples <= 0 ||
            manifest.tile_layout.tile_rows <= 0 || manifest.tile_layout.tile_columns <= 0)
            throw new InvalidDataException("Manifest tile layout is invalid.");
        if (manifest.tiles.Length != manifest.tile_layout.tile_count)
            throw new InvalidDataException(
                $"Manifest lists {manifest.tiles.Length} tiles but declares {manifest.tile_layout.tile_count}.");
    }

    private CachedTile GetTile(TileInfo tileInfo)
    {
        int key = tileInfo.tile_row * _manifest.tile_layout.tile_columns + tileInfo.tile_col;
        if (_cache.TryGetValue(key, out CachedTile cached))
        {
            _lru.Remove(cached.LruNode);
            _lru.AddFirst(cached.LruNode);
            return cached;
        }

        string path = Path.Combine(DatasetDirectory, tileInfo.file);
        if (!File.Exists(path))
            throw new FileNotFoundException($"LOLA tile not found: {path}", path);

        byte[] bytes = File.ReadAllBytes(path);
        int rows = tileInfo.stored_with_halo.rows;
        int columns = tileInfo.stored_with_halo.columns;
        long expectedBytes = (long)rows * columns * sizeof(short);
        if (bytes.LongLength != expectedBytes ||
            (tileInfo.file_size_bytes > 0 && bytes.LongLength != tileInfo.file_size_bytes))
            throw new InvalidDataException(
                $"Tile size mismatch for {tileInfo.file}: got {bytes.Length:N0} bytes, " +
                $"expected {expectedBytes:N0} bytes.");

        short[] samples = new short[rows * columns];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);

        LinkedListNode<int> node = _lru.AddFirst(key);
        cached = new CachedTile
        {
            Samples = samples,
            Rows = rows,
            Columns = columns,
            LruNode = node
        };
        _cache.Add(key, cached);

        while (_cache.Count > Math.Max(1, maxCachedTiles))
        {
            LinkedListNode<int> last = _lru.Last;
            int oldKey = last.Value;
            _lru.RemoveLast();
            _cache.Remove(oldKey);
        }

        return cached;
    }

    private short ReadSample(CachedTile tile, TileInfo tileInfo, int globalRow, int globalColumn)
    {
        int localRow = globalRow - tileInfo.stored_with_halo.row_start;
        int localColumn = PositiveModulo(
            globalColumn - tileInfo.stored_with_halo.column_start_wrapped,
            _manifest.source_grid.columns);

        if (localRow < 0 || localRow >= tile.Rows || localColumn < 0 || localColumn >= tile.Columns)
            throw new InvalidDataException(
                $"Tile halo does not contain source sample ({globalRow}, {globalColumn}) " +
                $"for tile ({tileInfo.tile_row}, {tileInfo.tile_col}).");

        return tile.Samples[localRow * tile.Columns + localColumn];
    }

    private static int PositiveModulo(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static double PositiveModulo(double value, double modulus)
    {
        double result = value % modulus;
        return result < 0.0 ? result + modulus : result;
    }
}

[Serializable]
public struct LolaElevationSample
{
    public double latitudeDeg;
    public double longitudeEastDeg;
    public double fractionalRow;
    public double fractionalColumn;
    public double dnBilinear;
    public double elevationMeters;
    public double radiusMeters;

    public LolaElevationSample(
        double latitude,
        double longitude,
        double row,
        double column,
        double dn,
        double elevation,
        double radius)
    {
        latitudeDeg = latitude;
        longitudeEastDeg = longitude;
        fractionalRow = row;
        fractionalColumn = column;
        dnBilinear = dn;
        elevationMeters = elevation;
        radiusMeters = radius;
    }
}
