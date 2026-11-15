using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds a LOLA-derived terrain version of the imported Moon mesh.
///
/// It keeps the source mesh topology, UVs, submeshes and materials, but moves every
/// source vertex radially to the elevation returned by LunarDEMReader. This is a
/// practical first global terrain pass: it gives the whole Moon LOLA-derived shape
/// without attempting to create hundreds of millions of vertices.
///
/// IMPORTANT: geometric detail between source vertices is still limited by the
/// imported GLB mesh density. The DEM query itself is bilinearly sampled from LDEM_64.
/// </summary>
public sealed class LunarTerrainMeshBuilder : MonoBehaviour
{
    [Header("Required components")]
    [SerializeField] private LunarDEMReader demReader;
    [SerializeField] private LunarReferenceFrame referenceFrame;

    [Header("Source visual Moon model")]
    [Tooltip("Assign the imported Moon_NASA_LRO_8k_Topo child, not the Moon parent. " +
             "All readable MeshFilters below this object will be converted.")]
    [SerializeField] private Transform sourceModelRoot;

    [Header("Generated terrain")]
    [SerializeField] private string generatedRootName = "Generated LOLA Terrain";
    [Tooltip("Additional radial offset in physical metres. Leave at zero for the scientific DEM surface.")]
    [SerializeField] private float terrainOffsetMeters = 0f;
    [Tooltip("Disable the original GLB renderers after a successful build. Original objects remain in the hierarchy.")]
    [SerializeField] private bool disableSourceRenderersAfterBuild = true;
    [Tooltip("Add MeshColliders to generated terrain pieces so clicks can hit the actual LOLA-derived mesh.")]
    [SerializeField] private bool addMeshCollidersForPicking = true;

    [Header("Diagnostics")]
    [SerializeField] private bool logPerMeshStatistics = true;
    [Min(1)] [SerializeField] private int progressLogInterval = 50000;

    private GameObject _generatedRoot;
    private readonly List<RendererState> _sourceRendererStates = new List<RendererState>();
    private readonly HashSet<Collider> _generatedTerrainColliders = new HashSet<Collider>();

    private struct RendererState
    {
        public Renderer renderer;
        public bool wasEnabled;
    }

    public bool HasGeneratedTerrain => _generatedRoot != null;
    public Transform GeneratedTerrainRoot => _generatedRoot != null ? _generatedRoot.transform : null;

    public bool IsGeneratedTerrainCollider(Collider collider)
        => collider != null && _generatedTerrainColliders.Contains(collider);

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnValidate()
    {
        progressLogInterval = Mathf.Max(1, progressLogInterval);
        if (string.IsNullOrWhiteSpace(generatedRootName))
            generatedRootName = "Generated LOLA Terrain";
    }

    private void OnDestroy()
    {
        RestoreSourceRenderers();
    }

    private void ResolveReferences()
    {
        if (demReader == null)
            demReader = GetComponent<LunarDEMReader>();
        if (referenceFrame == null)
            referenceFrame = GetComponent<LunarReferenceFrame>();
    }

    /// <summary>
    /// Runtime context-menu action. Enter Play Mode, select the Moon parent and run
    /// Build LOLA Terrain From Source Mesh.
    /// </summary>
    [ContextMenu("Build LOLA Terrain From Source Mesh")]
    public void BuildTerrainFromSourceMesh()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("Enter Play Mode before building runtime LOLA terrain. " +
                             "This avoids creating unsaved runtime meshes in the scene.", this);
            return;
        }

        ResolveReferences();
        if (!ValidateSetup(out string setupError))
        {
            Debug.LogError("LOLA terrain build cancelled: " + setupError, this);
            return;
        }

        if (!demReader.TryInitialize(out string demError))
        {
            Debug.LogError("LOLA terrain build cancelled: " + demError, this);
            return;
        }

        MeshFilter[] sourceFilters = sourceModelRoot.GetComponentsInChildren<MeshFilter>(true);
        if (sourceFilters.Length == 0)
        {
            Debug.LogError("No MeshFilters were found below Source Model Root.", this);
            return;
        }

        ClearGeneratedTerrainInternal(restoreSourceRenderers: true);
        _generatedRoot = new GameObject(generatedRootName);
        _generatedRoot.transform.SetParent(referenceFrame.MoonRoot, false);
        _generatedRoot.transform.localPosition = Vector3.zero;
        _generatedRoot.transform.localRotation = Quaternion.identity;
        _generatedRoot.transform.localScale = Vector3.one;

        int totalVertices = 0;
        long totalTriangles = 0;
        int processedVertices = 0;
        double minimumElevation = double.PositiveInfinity;
        double maximumElevation = double.NegativeInfinity;

        try
        {
            foreach (MeshFilter sourceFilter in sourceFilters)
            {
                if (sourceFilter == null || sourceFilter.sharedMesh == null)
                    continue;

                MeshRenderer sourceRenderer = sourceFilter.GetComponent<MeshRenderer>();
                if (sourceRenderer == null)
                {
                    Debug.LogWarning($"Skipping '{sourceFilter.name}': it has no MeshRenderer.", sourceFilter);
                    continue;
                }

                Mesh sourceMesh = sourceFilter.sharedMesh;
                if (!sourceMesh.isReadable)
                {
                    throw new InvalidOperationException(
                        $"Mesh '{sourceMesh.name}' is not readable. Select the imported Moon model asset " +
                        "in the Project window, enable Read/Write in its Model import settings, and Apply.");
                }

                Vector3[] sourceVertices = sourceMesh.vertices;
                Vector3[] terrainVertices = new Vector3[sourceVertices.Length];
                Transform moonRoot = referenceFrame.MoonRoot;

                for (int i = 0; i < sourceVertices.Length; i++)
                {
                    // Source mesh vertex -> Moon-root local coordinates.
                    Vector3 sourceWorld = sourceFilter.transform.TransformPoint(sourceVertices[i]);
                    Vector3 sourceMoonLocal = moonRoot.InverseTransformPoint(sourceWorld);

                    if (sourceMoonLocal.sqrMagnitude < 1e-12f)
                        throw new InvalidOperationException(
                            $"Mesh '{sourceMesh.name}' contains a vertex at the lunar centre.");

                    // Reverse the project's documented GLB reflection to obtain the
                    // scientific LOLA body-fixed direction, then derive latitude/longitude.
                    LunarCartesianPosition scientificDirection =
                        referenceFrame.MoonLocalToScientific(sourceMoonLocal);
                    LunarGeographicCoordinate geographic =
                        LunarCoordinates.ScientificCartesianToGeographic(scientificDirection);

                    // Longitude is undefined at an exact pole. Use one consistent meridian
                    // there so duplicated pole vertices do not acquire different radii.
                    double longitude = Math.Abs(geographic.latitudeDeg) > 89.999999
                        ? 0.0
                        : geographic.longitudeEastDeg;

                    if (!demReader.TryGetElevation(
                            geographic.latitudeDeg, longitude,
                            out LolaElevationSample sample, out string queryError))
                    {
                        throw new InvalidOperationException(
                            $"LOLA query failed for mesh '{sourceMesh.name}', vertex {i}: {queryError}");
                    }

                    double targetRadius = sample.radiusMeters + terrainOffsetMeters;
                    LunarCartesianPosition scientificPosition =
                        LunarCoordinates.GeographicToScientificCartesian(
                            geographic.latitudeDeg, longitude, targetRadius);

                    terrainVertices[i] = referenceFrame.ScientificToMoonLocal(scientificPosition);
                    minimumElevation = Math.Min(minimumElevation, sample.elevationMeters);
                    maximumElevation = Math.Max(maximumElevation, sample.elevationMeters);
                    processedVertices++;

                    if (progressLogInterval > 0 && processedVertices % progressLogInterval == 0)
                    {
                        Debug.Log(
                            $"LOLA terrain progress: {processedVertices:N0} vertices processed...", this);
                    }
                }

                // Clone the source mesh so UVs, submeshes, material slots and topology remain intact.
                Mesh terrainMesh = Instantiate(sourceMesh);
                terrainMesh.name = sourceMesh.name + "_LOLA_Terrain";
                terrainMesh.vertices = terrainVertices;
                terrainMesh.RecalculateBounds();
                terrainMesh.RecalculateNormals();

                // Recalculate tangents when UV0 exists, which is needed by some normal-mapped materials.
                Vector2[] sourceUv = sourceMesh.uv;
                if (sourceUv != null && sourceUv.Length == terrainVertices.Length)
                    terrainMesh.RecalculateTangents();

                GameObject terrainObject = new GameObject(sourceFilter.gameObject.name + "_LOLA_Terrain");
                terrainObject.transform.SetParent(_generatedRoot.transform, false);
                terrainObject.transform.localPosition = Vector3.zero;
                terrainObject.transform.localRotation = Quaternion.identity;
                terrainObject.transform.localScale = Vector3.one;

                MeshFilter terrainFilter = terrainObject.AddComponent<MeshFilter>();
                terrainFilter.sharedMesh = terrainMesh;

                MeshRenderer terrainRenderer = terrainObject.AddComponent<MeshRenderer>();
                CopyRendererSettings(sourceRenderer, terrainRenderer);

                if (addMeshCollidersForPicking)
                {
                    MeshCollider terrainCollider = terrainObject.AddComponent<MeshCollider>();
                    terrainCollider.sharedMesh = terrainMesh;
                    _generatedTerrainColliders.Add(terrainCollider);
                }

                totalVertices += terrainMesh.vertexCount;
                totalTriangles += CountTriangles(terrainMesh);

                if (logPerMeshStatistics)
                {
                    Debug.Log(
                        $"Built '{terrainObject.name}': {terrainMesh.vertexCount:N0} vertices, " +
                        $"{CountTriangles(terrainMesh):N0} triangles, " +
                        $"{terrainMesh.subMeshCount} submesh(es).", terrainObject);
                }
            }

            if (totalVertices == 0)
                throw new InvalidOperationException(
                    "No terrain meshes were generated. Check that Source Model Root contains readable " +
                    "MeshFilters with MeshRenderers.");

            if (disableSourceRenderersAfterBuild)
                DisableSourceRenderers();

            Debug.Log(
                $"LOLA terrain build complete. {totalVertices:N0} vertices, {totalTriangles:N0} triangles. " +
                $"Sampled elevation range: {minimumElevation:F1} m to {maximumElevation:F1} m. " +
                $"Generated root: '{generatedRootName}'. " +
                "The terrain uses the source GLB topology and UVs; geometric detail between its vertices " +
                "is therefore limited by the source mesh density.", this);
        }
        catch (Exception ex)
        {
            ClearGeneratedTerrainInternal(restoreSourceRenderers: true);
            Debug.LogError("LOLA terrain build failed; partial generated terrain was removed. " + ex.Message, this);
        }
    }

    [ContextMenu("Inspect Source Moon Mesh Density")]
    public void InspectSourceMeshDensity()
    {
        ResolveReferences();
        if (sourceModelRoot == null)
        {
            Debug.LogError("Assign Source Model Root first.", this);
            return;
        }

        MeshFilter[] filters = sourceModelRoot.GetComponentsInChildren<MeshFilter>(true);
        int totalVertices = 0;
        int totalTriangles = 0;

        foreach (MeshFilter filter in filters)
        {
            if (filter == null || filter.sharedMesh == null)
                continue;

            Mesh mesh = filter.sharedMesh;
            long triangles = CountTriangles(mesh);
            totalVertices += mesh.vertexCount;
            totalTriangles += (int)Math.Min(int.MaxValue, triangles);

            Debug.Log(
                $"Source mesh '{filter.name}': readable={mesh.isReadable}, " +
                $"vertices={mesh.vertexCount:N0}, triangles={triangles:N0}, " +
                $"submeshes={mesh.subMeshCount}, index format={mesh.indexFormat}.", filter);
        }

        Debug.Log(
            $"Source Moon total: {totalVertices:N0} vertices and {totalTriangles:N0} triangles. " +
            "The LOLA terrain mesh will have the same vertex counts. If the count is modest, " +
            "we should add higher-resolution DEM mesh patches in a later step.", this);
    }

    [ContextMenu("Clear Generated LOLA Terrain")]
    public void ClearGeneratedTerrain()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("Clear generated runtime terrain while in Play Mode.", this);
            return;
        }
        ClearGeneratedTerrainInternal(restoreSourceRenderers: true);
        Debug.Log("Generated LOLA terrain cleared; source Moon renderers restored.", this);
    }

    private bool ValidateSetup(out string error)
    {
        if (demReader == null)
        {
            error = "LunarDEMReader is not assigned.";
            return false;
        }
        if (referenceFrame == null)
        {
            error = "LunarReferenceFrame is not assigned.";
            return false;
        }
        if (sourceModelRoot == null)
        {
            error = "Source Model Root is not assigned. Assign the imported Moon GLB child.";
            return false;
        }
        if (sourceModelRoot == referenceFrame.MoonRoot)
        {
            error = "Source Model Root must be the imported GLB child, not the Moon parent.";
            return false;
        }
        error = null;
        return true;
    }

    private void DisableSourceRenderers()
    {
        _sourceRendererStates.Clear();
        Renderer[] renderers = sourceModelRoot.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer sourceRenderer in renderers)
        {
            _sourceRendererStates.Add(new RendererState
            {
                renderer = sourceRenderer,
                wasEnabled = sourceRenderer.enabled
            });
            sourceRenderer.enabled = false;
        }
    }

    private void RestoreSourceRenderers()
    {
        foreach (RendererState state in _sourceRendererStates)
        {
            if (state.renderer != null)
                state.renderer.enabled = state.wasEnabled;
        }
        _sourceRendererStates.Clear();
    }

    private void ClearGeneratedTerrainInternal(bool restoreSourceRenderers)
    {
        if (_generatedRoot != null)
        {
            // Runtime-only objects are intentionally generated during Play Mode.
            Destroy(_generatedRoot);
            _generatedRoot = null;
        }
        _generatedTerrainColliders.Clear();

        if (restoreSourceRenderers)
            RestoreSourceRenderers();
    }

    private static long CountTriangles(Mesh mesh)
    {
        long count = 0;
        for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
        {
            if (mesh.GetTopology(submesh) == MeshTopology.Triangles)
                count += (long)mesh.GetIndexCount(submesh) / 3L;
        }
        return count;
    }

    private static void CopyRendererSettings(MeshRenderer source, MeshRenderer destination)
    {
        destination.sharedMaterials = source.sharedMaterials;
        destination.shadowCastingMode = source.shadowCastingMode;
        destination.receiveShadows = source.receiveShadows;
        destination.lightProbeUsage = source.lightProbeUsage;
        destination.reflectionProbeUsage = source.reflectionProbeUsage;
        destination.motionVectorGenerationMode = source.motionVectorGenerationMode;
        destination.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
        destination.renderingLayerMask = source.renderingLayerMask;
        destination.sortingLayerID = source.sortingLayerID;
        destination.sortingOrder = source.sortingOrder;
        destination.probeAnchor = source.probeAnchor;
    }
}
