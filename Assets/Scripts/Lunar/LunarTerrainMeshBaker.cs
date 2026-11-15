using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

/// <summary>
/// Editor-time baker for a permanent LOLA-displaced version of the imported Moon GLB.
/// Creates standalone Mesh assets under Assets/ and a generated hierarchy under MoonRoot.
/// The original GLB objects and meshes are preserved; their renderers are optionally
/// disabled only after every generated mesh has been saved successfully.
///
/// This reuses the source GLB topology. The LOLA DEM is sampled at every source vertex,
/// so detail between those vertices is interpolated by the renderer rather than represented
/// as individual LDEM_64 grid cells.
/// </summary>
[DisallowMultipleComponent]
public sealed class LunarTerrainMeshBaker : MonoBehaviour
{
    [Header("Required components")]
    [SerializeField] private LunarDEMReader demReader;
    [SerializeField] private LunarReferenceFrame referenceFrame;

    [Header("Source model")]
    [Tooltip("Assign the imported Moon_NASA_LRO_8k_Topo child, not the Moon parent.")]
    [SerializeField] private Transform sourceModelRoot;

    [Header("Baked output")]
    [SerializeField] private string generatedRootName = "Generated LOLA Terrain";
    [Tooltip("Must be inside the Unity project's Assets folder.")]
    [SerializeField] private string meshAssetFolder = "Assets/Generated/LunarTerrain";
    [SerializeField] private float terrainOffsetMeters = 0f;
    [Tooltip("Disable original GLB renderers after every mesh asset has been created successfully.")]
    [SerializeField] private bool disableSourceRenderersAfterBake = true;
    [Tooltip("Recalculate tangents for meshes with UV0. Enable for materials using normal maps.")]
    [SerializeField] private bool recalculateTangents = true;

    [Header("Collision")]
    [Tooltip("Leave OFF for the 8-million-vertex global mesh. Use the Moon SphereCollider for directional picking.")]
    [SerializeField] private bool addMeshColliders = false;

    [Header("Editor processing")]
    [Min(1000)] [SerializeField] private int progressUpdateInterval = 100000;

    [Header("Generated scene reference")]
    [SerializeField, HideInInspector] private GameObject generatedTerrainRoot;
    [SerializeField, HideInInspector] private List<Renderer> renderersDisabledByBake = new List<Renderer>();

    public GameObject GeneratedTerrainRoot => generatedTerrainRoot;

    private void OnValidate()
    {
        progressUpdateInterval = Mathf.Max(1000, progressUpdateInterval);
        if (string.IsNullOrWhiteSpace(generatedRootName)) generatedRootName = "Generated LOLA Terrain";
        if (string.IsNullOrWhiteSpace(meshAssetFolder)) meshAssetFolder = "Assets/Generated/LunarTerrain";
    }

    [ContextMenu("Bake Permanent LOLA Terrain Mesh Assets")]
    public void BakePermanentTerrain()
    {
#if UNITY_EDITOR
        BakePermanentTerrainEditor();
#else
        Debug.LogError("Permanent LOLA mesh baking is an Editor-only operation.", this);
#endif
    }

    [ContextMenu("Restore Original Moon Renderers")]
    public void RestoreOriginalRenderers()
    {
#if UNITY_EDITOR
        Undo.RecordObject(this, "Restore Original Moon Renderers");
        int restored = 0;
        foreach (Renderer sourceRenderer in renderersDisabledByBake)
        {
            if (sourceRenderer == null) continue;
            Undo.RecordObject(sourceRenderer, "Restore Original Moon Renderer");
            sourceRenderer.enabled = true;
            EditorUtility.SetDirty(sourceRenderer);
            restored++;
        }
        renderersDisabledByBake.Clear();
        EditorUtility.SetDirty(this);
        if (gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(gameObject.scene);
        Debug.Log($"Restored {restored} original Moon renderer(s). The baked terrain was not removed.", this);
#else
        Debug.LogWarning("Renderer restoration is intended for the Unity Editor.", this);
#endif
    }

    [ContextMenu("Remove Baked Terrain From Scene (Keep Mesh Assets)")]
    public void RemoveBakedTerrainFromScene()
    {
#if UNITY_EDITOR
        if (generatedTerrainRoot == null)
        {
            Debug.Log("No generated terrain root is assigned.", this);
            return;
        }

        GameObject rootToRemove = generatedTerrainRoot;
        Undo.RecordObject(this, "Remove Baked LOLA Terrain Reference");
        generatedTerrainRoot = null;
        EditorUtility.SetDirty(this);
        Undo.DestroyObjectImmediate(rootToRemove);
        if (gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(gameObject.scene);
        Debug.Log("Removed generated terrain from the scene. Mesh assets were kept in the Project.", this);
#else
        Debug.LogWarning("Terrain removal is intended for the Unity Editor.", this);
#endif
    }

#if UNITY_EDITOR
    private void BakePermanentTerrainEditor()
    {
        if (!ValidateSetup(out string setupError))
        {
            Debug.LogError("LOLA mesh bake cancelled: " + setupError, this);
            return;
        }

        if (generatedTerrainRoot != null || transform.Find(generatedRootName) != null)
        {
            Debug.LogError(
                $"A child named '{generatedRootName}' already exists or is assigned. Remove the existing generated terrain from the scene first. Existing mesh assets are not deleted automatically.", this);
            return;
        }

        if (!demReader.TryInitialize(out string demError))
        {
            Debug.LogError("LOLA mesh bake cancelled: " + demError, this);
            return;
        }

        if (!meshAssetFolder.Replace('\\', '/').StartsWith("Assets/", StringComparison.Ordinal) &&
            !string.Equals(meshAssetFolder.Replace('\\', '/'), "Assets", StringComparison.Ordinal))
        {
            Debug.LogError("Mesh Asset Folder must be inside the Unity project's Assets directory.", this);
            return;
        }

        MeshFilter[] sourceFilters = sourceModelRoot.GetComponentsInChildren<MeshFilter>(true);
        if (sourceFilters.Length == 0)
        {
            Debug.LogError("No MeshFilters were found under Source Model Root.", this);
            return;
        }

        Directory.CreateDirectory(meshAssetFolder);
        AssetDatabase.Refresh();

        GameObject root = null;
        List<string> createdAssetPaths = new List<string>();
        List<Renderer> renderersToDisable = new List<Renderer>();
        int totalVertices = 0;
        long totalTriangles = 0;
        double minElevation = double.PositiveInfinity;
        double maxElevation = double.NegativeInfinity;
        int processed = 0;
        bool completed = false;

        try
        {
            root = new GameObject(generatedRootName);
            Undo.RegisterCreatedObjectUndo(root, "Create Baked LOLA Terrain Root");
            root.transform.SetParent(referenceFrame.MoonRoot, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            for (int filterIndex = 0; filterIndex < sourceFilters.Length; filterIndex++)
            {
                MeshFilter sourceFilter = sourceFilters[filterIndex];
                if (sourceFilter == null || sourceFilter.sharedMesh == null) continue;

                MeshRenderer sourceRenderer = sourceFilter.GetComponent<MeshRenderer>();
                if (sourceRenderer == null) continue;

                Mesh sourceMesh = sourceFilter.sharedMesh;
                if (!sourceMesh.isReadable)
                    throw new InvalidOperationException(
                        $"Mesh '{sourceMesh.name}' is not readable. Enable Read/Write in the GLB Model import settings and Apply.");

                EditorUtility.DisplayProgressBar(
                    "Baking LOLA lunar terrain",
                    $"Preparing {sourceFilter.name} ({filterIndex + 1}/{sourceFilters.Length})",
                    (float)filterIndex / Math.Max(1, sourceFilters.Length));

                Vector3[] sourceVertices = sourceMesh.vertices;
                Vector3[] displacedVertices = new Vector3[sourceVertices.Length];
                Transform moonRoot = referenceFrame.MoonRoot;

                for (int vertexIndex = 0; vertexIndex < sourceVertices.Length; vertexIndex++)
                {
                    Vector3 sourceWorld = sourceFilter.transform.TransformPoint(sourceVertices[vertexIndex]);
                    Vector3 sourceMoonLocal = moonRoot.InverseTransformPoint(sourceWorld);
                    if (sourceMoonLocal.sqrMagnitude < 1e-12f)
                        throw new InvalidOperationException(
                            $"Mesh '{sourceMesh.name}' has a vertex at the lunar centre (index {vertexIndex}).");

                    LunarCartesianPosition scientificDirection = referenceFrame.MoonLocalToScientific(sourceMoonLocal);
                    LunarGeographicCoordinate geographic =
                        LunarCoordinates.ScientificCartesianToGeographic(scientificDirection);

                    double longitude = Math.Abs(geographic.latitudeDeg) > 89.999999
                        ? 0.0
                        : geographic.longitudeEastDeg;

                    if (!demReader.TryGetElevation(
                            geographic.latitudeDeg, longitude,
                            out LolaElevationSample sample, out string queryError))
                        throw new InvalidOperationException(
                            $"LOLA query failed for '{sourceMesh.name}', vertex {vertexIndex}: {queryError}");

                    LunarCartesianPosition scientificPosition =
                        LunarCoordinates.GeographicToScientificCartesian(
                            geographic.latitudeDeg,
                            longitude,
                            sample.radiusMeters + terrainOffsetMeters);

                    displacedVertices[vertexIndex] =
                        referenceFrame.ScientificToMoonLocal(scientificPosition);

                    minElevation = Math.Min(minElevation, sample.elevationMeters);
                    maxElevation = Math.Max(maxElevation, sample.elevationMeters);
                    processed++;

                    if (processed % progressUpdateInterval == 0 || vertexIndex == sourceVertices.Length - 1)
                    {
                        float meshProgress = (float)(vertexIndex + 1) / Math.Max(1, sourceVertices.Length);
                        float overallProgress = (filterIndex + meshProgress) / Math.Max(1, sourceFilters.Length);
                        bool cancel = EditorUtility.DisplayCancelableProgressBar(
                            "Baking LOLA lunar terrain",
                            $"{sourceFilter.name}: {vertexIndex + 1:N0}/{sourceVertices.Length:N0} vertices; " +
                            $"{processed:N0} total",
                            overallProgress);
                        if (cancel) throw new OperationCanceledException("Terrain baking was cancelled by the user.");
                    }
                }

                Mesh bakedMesh = Instantiate(sourceMesh);
                bakedMesh.name = sourceMesh.name + "_LOLA_Terrain";
                // Instantiate preserves the source mesh's index format and all submesh index buffers.
                // Do not change indexFormat here: changing it resets the index buffer/submesh state.
                bakedMesh.vertices = displacedVertices;
                bakedMesh.RecalculateBounds();
                bakedMesh.RecalculateNormals();

                if (recalculateTangents)
                {
                    Vector2[] uv0 = sourceMesh.uv;
                    if (uv0 != null && uv0.Length == displacedVertices.Length)
                        bakedMesh.RecalculateTangents();
                }

                string assetName = MakeSafeFileName(
                    $"{filterIndex:D2}_{sourceFilter.name}_{sourceMesh.name}_LOLA_Terrain");
                string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                    $"{meshAssetFolder.TrimEnd('/', '\\')}/{assetName}.asset");
                AssetDatabase.CreateAsset(bakedMesh, assetPath);
                createdAssetPaths.Add(assetPath);

                GameObject terrainObject = new GameObject(sourceFilter.gameObject.name + "_LOLA_Terrain");
                Undo.RegisterCreatedObjectUndo(terrainObject, "Create Baked LOLA Terrain Mesh Object");
                terrainObject.transform.SetParent(root.transform, false);
                terrainObject.transform.localPosition = Vector3.zero;
                terrainObject.transform.localRotation = Quaternion.identity;
                terrainObject.transform.localScale = Vector3.one;
                terrainObject.layer = sourceFilter.gameObject.layer;

                MeshFilter bakedFilter = terrainObject.AddComponent<MeshFilter>();
                bakedFilter.sharedMesh = bakedMesh;
                MeshRenderer bakedRenderer = terrainObject.AddComponent<MeshRenderer>();
                CopyRendererSettings(sourceRenderer, bakedRenderer);

                if (addMeshColliders)
                {
                    if (bakedMesh.vertexCount > 2_000_000)
                        Debug.LogWarning(
                            $"Adding a MeshCollider to '{bakedMesh.name}' with {bakedMesh.vertexCount:N0} vertices may be expensive. Consider leaving Mesh Colliders disabled and using the Moon SphereCollider for picking.", terrainObject);
                    MeshCollider meshCollider = terrainObject.AddComponent<MeshCollider>();
                    meshCollider.sharedMesh = bakedMesh;
                }

                totalVertices += bakedMesh.vertexCount;
                totalTriangles += CountTriangles(bakedMesh);
                if (sourceRenderer.enabled) renderersToDisable.Add(sourceRenderer);

                Debug.Log(
                    $"Baked mesh asset '{assetPath}': {bakedMesh.vertexCount:N0} vertices, " +
                    $"{CountTriangles(bakedMesh):N0} triangles, index format {bakedMesh.indexFormat}.",
                    terrainObject);
            }

            if (totalVertices == 0)
                throw new InvalidOperationException(
                    "No meshes were baked. Ensure Source Model Root contains readable MeshFilters with MeshRenderers.");

            AssetDatabase.SaveAssets();

            // Disable originals only after all output meshes have been created successfully.
            if (disableSourceRenderersAfterBake)
            {
                Undo.RecordObject(this, "Record Original Moon Renderers");
                renderersDisabledByBake.Clear();
                foreach (Renderer sourceRenderer in renderersToDisable)
                {
                    Undo.RecordObject(sourceRenderer, "Disable Original Moon Renderer");
                    sourceRenderer.enabled = false;
                    EditorUtility.SetDirty(sourceRenderer);
                    renderersDisabledByBake.Add(sourceRenderer);
                }
                EditorUtility.SetDirty(this);
            }

            generatedTerrainRoot = root;
            EditorUtility.SetDirty(this);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            completed = true;

            Debug.Log(
                $"Permanent LOLA terrain bake completed: {totalVertices:N0} vertices, " +
                $"{totalTriangles:N0} triangles. Sampled vertex elevations: " +
                $"{minElevation:F1} m to {maxElevation:F1} m.\n" +
                $"Mesh assets saved under '{meshAssetFolder}'. Original GLB meshes/assets were preserved.", this);
        }
        catch (OperationCanceledException ex)
        {
            Debug.LogWarning(ex.Message + " Partial generated scene objects and mesh assets were removed.", this);
        }
        catch (Exception ex)
        {
            Debug.LogError("Permanent LOLA terrain bake failed. " + ex, this);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            if (!completed)
            {
                if (root != null) Undo.DestroyObjectImmediate(root);
                foreach (string assetPath in createdAssetPaths)
                    AssetDatabase.DeleteAsset(assetPath);
                AssetDatabase.SaveAssets();
            }
            AssetDatabase.Refresh();
        }
    }

    private bool ValidateSetup(out string error)
    {
        if (demReader == null) { error = "Assign the LunarDEMReader component."; return false; }
        if (referenceFrame == null) { error = "Assign the LunarReferenceFrame component."; return false; }
        if (sourceModelRoot == null) { error = "Assign the imported Moon GLB child as Source Model Root."; return false; }
        if (sourceModelRoot == referenceFrame.MoonRoot)
        {
            error = "Source Model Root must be the imported GLB child, not the Moon parent.";
            return false;
        }
        if (referenceFrame.MoonRoot == null) { error = "Moon Root is not assigned."; return false; }
        error = null;
        return true;
    }

    private static int CountTriangles(Mesh mesh)
    {
        long count = 0;
        for (int i = 0; i < mesh.subMeshCount; i++)
            if (mesh.GetTopology(i) == MeshTopology.Triangles)
                count += (long)mesh.GetIndexCount(i) / 3L;
        return (int)Math.Min(int.MaxValue, count);
    }

    private static string MakeSafeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
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
#endif
}
