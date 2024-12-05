﻿using UnityEngine;
using UnityEngine.SceneManagement;
using System;
using System.Collections.Generic;
using UnityEngine.Rendering;
using System.Collections;
using System.Reflection;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
using System.IO;
#endif

[ExecuteInEditMode]
public class LevelLightmapData : MonoBehaviour
{

	[System.Serializable]
	public class RendererInfo
	{
		public Renderer renderer;
        public int transformHash;
        public string name;
        public int lightmapIndex;
		public Vector4 lightmapScaleOffset;
	}

    public bool latestBuildHasReltimeLights;
    [Tooltip("Enable this if you want to allow the script to load a lighting scene additively. This is useful when the scene contains a light set to realtime or mixed mode or reflection probes. If you're managing the scenes loading yourself you should disable it.")]
    public bool allowLoadingLightingScenes = true;
    [Tooltip("Enable this if you want to use different lightmap resolutions in your different lighting scenarios. In that case you'll have to disable Static Batching in the Player Settings. When disabled, Static Batching can be used but all your lighting scenarios need to use the same lightmap resolution.")]
    public bool applyLightmapScaleAndOffset = true;

	[SerializeField]
	List<LightingScenarioData> lightingScenariosData;

#if UNITY_EDITOR
    [SerializeField]
	public List<SceneAsset> lightingScenariosScenes;
#endif
    public string currentLightingScenario = "";
    public string previousLightingScenario = "";

    private Coroutine m_SwitchSceneCoroutine;

    [SerializeField]
    public int lightingScenariosCount;

    //TODO : enable logs only when verbose enabled
    public bool verbose = false;

    static string messagePrefix = "Lightmap Switching Tool - ";

    public void LoadLightingScenario(int index)
    {
        var dataToLoad = lightingScenariosData[index];

        LoadLightingScenarioData(dataToLoad);
    }

    public void LoadLightingScenario(string name)
    {
        var data = lightingScenariosData.Find(x => x.name.Equals(name));
        if(data == null)
        {
            Debug.LogError(messagePrefix+"Can't find lighting scenario with name (case sensitive) " + name);
            return;
        }
        LoadLightingScenario(data);
    }

    public void LoadLightingScenario(LightingScenarioData data)
    {
        if (data.name != currentLightingScenario)
        {
            previousLightingScenario = currentLightingScenario;

            currentLightingScenario = data.name;

            LightmapSettings.lightmapsMode = data.lightmapsMode;

            if (allowLoadingLightingScenes)
                m_SwitchSceneCoroutine = StartCoroutine(SwitchSceneCoroutine(lightingScenariosData.Find(x => x.name == previousLightingScenario)?.lightingSceneName, lightingScenariosData.Find(x => x.name == currentLightingScenario)?.lightingSceneName));

            var newLightmaps = LoadLightmaps(data);

            ApplyDataRendererInfo(data.rendererInfos);

            LightmapSettings.lightmaps = newLightmaps;

            LoadLightProbes(data);
        }
    }

    public void LoadLightingScenarioData(LightingScenarioData data)
    {
        LoadLightingScenario(data);
    }

    public void LoadAssetBundleByName(string name)
    {
        AssetBundle assetBundle = AssetBundle.LoadFromFile(Application.streamingAssetsPath + "/" + name);
        Debug.Log(assetBundle == null ? messagePrefix+"Failed to load Asset Bundle" : "Lightmap switching tool - Asset bundle loaded succesfully");
        assetBundle.LoadAllAssets();
    }

    public void RefreshLightingScenarios()
    {
        lightingScenariosData = Resources.FindObjectsOfTypeAll<LightingScenarioData>().Where(x => x.geometrySceneName == gameObject.scene.name).ToList();
        Debug.Log(messagePrefix + "Loaded " + lightingScenariosData.Count + " suitable lighting scenarios.");
        foreach (var scene in lightingScenariosData)
        {
            Debug.Log(scene.name);
        }
    }


#if UNITY_EDITOR

    // In editor only we cache the baked probe data when entering playmode, and reset it on exit
    // This negates runtime changes that the LevelLightmapData library creates in the lighting asset loaded into the starting scene 

    UnityEngine.Rendering.SphericalHarmonicsL2[] cachedBakedProbeData = null;

    public void OnEnteredPlayMode_EditorOnly()
    {
        if(LightmapSettings.lightProbes != null)
        {
            cachedBakedProbeData = LightmapSettings.lightProbes.bakedProbes;
            Debug.Log(messagePrefix+"Caching editor lightProbes");
        }
    }

    public void OnExitingPlayMode_EditorOnly()
    {
        // Only do this cache restore if we have probe data of matching length
        if (cachedBakedProbeData != null && LightmapSettings.lightProbes.bakedProbes.Length == cachedBakedProbeData.Length)
        {
            LightmapSettings.lightProbes.bakedProbes = cachedBakedProbeData;
            Debug.Log(messagePrefix+"Restoring editor lightProbes");
        }
    }

#endif

    IEnumerator SwitchSceneCoroutine(string sceneToUnload, string sceneToLoad)
    {
        AsyncOperation unloadop = null;
        AsyncOperation loadop = null;

        if (sceneToUnload != null && sceneToUnload != string.Empty && sceneToUnload != sceneToLoad)
        {
            unloadop = SceneManager.UnloadSceneAsync(sceneToUnload);
            while (!unloadop.isDone)
            {
                yield return new WaitForEndOfFrame();
            }
        }

        if(sceneToLoad != null && sceneToLoad != string.Empty && sceneToLoad != "")
        {
            loadop = SceneManager.LoadSceneAsync(sceneToLoad, LoadSceneMode.Additive);
            while ((!loadop.isDone || loadop == null))
            {
                yield return new WaitForEndOfFrame();
            }   
            SceneManager.SetActiveScene(SceneManager.GetSceneByName(sceneToLoad));
        }
    }

    LightmapData[] LoadLightmaps(int index)
    {
        if (lightingScenariosData[index].lightmaps == null
                || lightingScenariosData[index].lightmaps.Length == 0)
        {
            Debug.LogWarning( messagePrefix + "No lightmaps stored in scenario " + index);
            return null;
        }

        var newLightmaps = new LightmapData[lightingScenariosData[index].lightmaps.Length];

        for (int i = 0; i < newLightmaps.Length; i++)
        {
            newLightmaps[i] = new LightmapData();
            newLightmaps[i].lightmapColor = lightingScenariosData[index].lightmaps[i];

            if (lightingScenariosData[index].lightmapsMode != LightmapsMode.NonDirectional)
            {
                newLightmaps[i].lightmapDir = lightingScenariosData[index].lightmapsDir[i];
            }
            if (lightingScenariosData[index].shadowMasks.Length > 0)
            {
                newLightmaps[i].shadowMask = lightingScenariosData[index].shadowMasks[i];
            }
        }

        return newLightmaps;
    }

    LightmapData[] LoadLightmaps(LightingScenarioData data)
    {
        if (data.lightmaps == null
                || data.lightmaps.Length == 0)
        {
            Debug.LogWarning("No lightmaps stored in scenario " + data.name);
            return null;
        }

        var newLightmaps = new LightmapData[data.lightmaps.Length];

        for (int i = 0; i < newLightmaps.Length; i++)
        {
            newLightmaps[i] = new LightmapData();
            newLightmaps[i].lightmapColor = data.lightmaps[i];

            if (data.lightmapsMode != LightmapsMode.NonDirectional)
            {
                newLightmaps[i].lightmapDir = data.lightmapsDir[i];
            }
            if (data.shadowMasks.Length > 0)
            {
                newLightmaps[i].shadowMask = data.shadowMasks[i];
            }
        }

        return newLightmaps;
    }

    public void ApplyDataRendererInfo(RendererInfo[] infos)
    {
        try
        {
            var hashRendererPairs = new Dictionary<int, RendererInfo>();

            //Fill with lighting scenario to load renderer infos
            foreach (var info in infos)
            {
                var uniquehash = info.transformHash + info.name.GetHashCode();
                if (hashRendererPairs.ContainsKey(uniquehash))
                    Debug.LogWarning(messagePrefix + "This renderer info could not be matched. Please check that you don't have 2 gameobjects with the same name, transform, and mesh.", info.renderer);
                else
                    hashRendererPairs.Add(uniquehash, info);
            }

            //Find all renderers
            var renderers = FindObjectsOfType<Renderer>();

            //Apply stored scale and offset if transform and mesh hashes match
            foreach (var render in renderers)
            {
                var infoToApply = new RendererInfo();
                if (hashRendererPairs.TryGetValue(GetStableHash(render.gameObject.transform) + render.gameObject.name.GetHashCode(), out infoToApply))
                {
                    render.lightmapIndex = infoToApply.lightmapIndex;
                    if (applyLightmapScaleAndOffset)
                        render.lightmapScaleOffset = infoToApply.lightmapScaleOffset;
                }
                else
                    Debug.LogWarning(messagePrefix + "Couldn't find renderer info for " + render.gameObject.name + ". This can be ignored if it's not supposed to receive any lightmap.", render);
            }

            //Find all renderers
            var terrains = FindObjectsOfType<Terrain>();

            //Apply stored scale and offset if transform and mesh hashes match
            foreach (var terrain in terrains)
            {
                var infoToApply = new RendererInfo();

                //int transformHash = render.gameObject.transform.position

                if (hashRendererPairs.TryGetValue(GetStableHash(terrain.gameObject.transform) + terrain.name.GetHashCode() + terrain.terrainData.GetHashCode(), out infoToApply))
                {
                    if (terrain.gameObject.name == infoToApply.name)
                    {
                        terrain.lightmapIndex = infoToApply.lightmapIndex;
                        if (applyLightmapScaleAndOffset)
                            terrain.lightmapScaleOffset = infoToApply.lightmapScaleOffset;
                    }
                }
            }

        }
        catch (Exception e)
        {
            if (Application.isEditor)
                Debug.LogError(messagePrefix + "Error in ApplyDataRendererInfo:" + e.GetType().ToString());
        }
    }
    public void ApplyDataRendererInfo(int index)
    {
        if (lightingScenariosData[index] != null)
            ApplyDataRendererInfo(lightingScenariosData[index].rendererInfos);
        else
            Debug.LogWarning(messagePrefix + "Trying to load null lighting scenario data at index " + index);
    }

    public void LoadLightProbes(int index)
    {
        if (lightingScenariosData[index] != null)
            LoadLightProbes(lightingScenariosData[index]);
        else
            Debug.LogWarning(messagePrefix + "Trying to load null lighting scenario data at index " + index);
    }

    public void LoadLightProbes(LightingScenarioData data)
    {
        if(data.lightProbesAsset.coefficients.Length > 0)
        {
            try
            {
                LightmapSettings.lightProbes = data.lightProbesAsset.lightprobes;
                LightmapSettings.lightProbes.bakedProbes = data.lightProbesAsset.lightprobes.bakedProbes;
            }
            catch { Debug.LogWarning("Warning, error when trying to load lightprobes for scenario " + data.name); }
        }
    }
    public static int GetStableHash(Transform transform)
    {
        Vector3 stablePos = new Vector3(LimitDecimals(transform.position.x, 2), LimitDecimals(transform.position.y, 2), LimitDecimals(transform.position.z, 2));
        Vector3 stableRot = new Vector3(LimitDecimals(transform.rotation.x, 1), LimitDecimals(transform.rotation.y, 1), LimitDecimals(transform.rotation.z, 1));
        return stablePos.GetHashCode() + stableRot.GetHashCode();
    }
    static float LimitDecimals(float input, int decimalcount)
    {
        var multiplier = Mathf.Pow(10, decimalcount);
        return Mathf.Floor(input * multiplier) / multiplier;
    }
    public void StoreLightmapInfos(int index)
    {
        LightingScenarioData newLightingScenarioData;
        while (lightingScenariosData.Count < index + 1)
            lightingScenariosData.Add(null);
        if (lightingScenariosData[index] != null)
            newLightingScenarioData = lightingScenariosData[index];
        else
             newLightingScenarioData = ScriptableObject.CreateInstance<LightingScenarioData>();

        var newRendererInfos = new List<RendererInfo>();
        var newLightmapsTextures = new List<Texture2D>();
        var newLightmapsTexturesDir = new List<Texture2D>();
        var newLightmapsMode = LightmapSettings.lightmapsMode;
        var newLightmapsShadowMasks = new List<Texture2D>();

#if UNITY_EDITOR
        newLightingScenarioData.lightingSceneName = lightingScenariosScenes[index].name;
        newLightingScenarioData.name = newLightingScenarioData.lightingSceneName;
#endif
        newLightingScenarioData.geometrySceneName = gameObject.scene.name;
        newLightingScenarioData.storeRendererInfos = true;

        GenerateLightmapInfo(gameObject, newRendererInfos, newLightmapsTextures, newLightmapsTexturesDir, newLightmapsShadowMasks, newLightmapsMode);

        newLightingScenarioData.lightmapsMode = newLightmapsMode;

		newLightingScenarioData.lightmaps = newLightmapsTextures.ToArray();

		if (newLightmapsMode != LightmapsMode.NonDirectional)
        {
			newLightingScenarioData.lightmapsDir = newLightmapsTexturesDir.ToArray();
        }

        //Mixed or realtime support
        newLightingScenarioData.hasRealtimeLights = latestBuildHasReltimeLights;

        newLightingScenarioData.shadowMasks = newLightmapsShadowMasks.ToArray();

        newLightingScenarioData.rendererInfos = newRendererInfos.ToArray();

        if (newLightingScenarioData.lightProbesAsset == null)
        {
            var probes = ScriptableObject.CreateInstance<LightProbesAsset>();
            newLightingScenarioData.lightProbesAsset = probes;
        }

        newLightingScenarioData.lightProbesAsset.coefficients = LightmapSettings.lightProbes.bakedProbes;
        newLightingScenarioData.lightProbesAsset.lightprobes = LightProbes.Instantiate(LightmapSettings.lightProbes);
        newLightingScenarioData.lightProbesAsset.lightprobes.name = newLightingScenarioData.lightingSceneName + "_probes";

        if (lightingScenariosData.Count < index + 1)
        {
            lightingScenariosData.Insert(index, newLightingScenarioData);
        }
        else
        {
            lightingScenariosData[index] = newLightingScenarioData;
        }

        lightingScenariosCount = lightingScenariosData.Count;
    }

    static void GenerateLightmapInfo(GameObject root, List<RendererInfo> newRendererInfos, List<Texture2D> newLightmapsLight, List<Texture2D> newLightmapsDir, List<Texture2D> newLightmapsShadow, LightmapsMode newLightmapsMode)
    {
        var gameObjects = FindObjectsOfType<GameObject>().Where(x => x.GetComponent<Renderer>() != null || x.GetComponent<Terrain>() != null);

        newLightmapsMode = LightmapSettings.lightmapsMode;

        foreach (var go in gameObjects)
        {
            Terrain t;
            Renderer r;
            MeshFilter m;
            go.TryGetComponent<Renderer>(out r);
            go.TryGetComponent<Terrain>(out t);
            go.TryGetComponent<MeshFilter>(out m);

            RendererInfo rendererInfo = new RendererInfo()
            {
                name = go.name,
                transformHash = GetStableHash(go.transform),
                lightmapScaleOffset = r ? r.lightmapScaleOffset : t.lightmapScaleOffset,
                lightmapIndex = r ? r.lightmapIndex : t.lightmapIndex,
                renderer = r ? r : null,
            };
            newRendererInfos.Add(rendererInfo);
        }
        LightmapData[] datas = LightmapSettings.lightmaps;
        foreach (var data in datas)
        {
            if (data.lightmapColor != null)
                newLightmapsLight.Add(data.lightmapColor);
            if(data.lightmapDir != null)
                newLightmapsDir.Add(data.lightmapDir);
            if(data.shadowMask != null)
                newLightmapsShadow.Add(data.shadowMask);
        }

        if (Application.isEditor)
            Debug.Log(messagePrefix + "Stored info for " + gameObjects.ToList().Count + " GameObjects.");
        /*
        Terrain[] terrains = FindObjectsOfType<Terrain>();
        foreach (Terrain terrain in terrains) 
        {
            if (terrain != null && terrain.lightmapIndex != -1 && terrain.lightmapIndex != 65534)
            {
                RendererInfo terrainRendererInfo = new RendererInfo();
                terrainRendererInfo.name = terrain.gameObject.name;
                terrainRendererInfo.lightmapScaleOffset = terrain.lightmapScaleOffset;
                terrainRendererInfo.transformHash = GetStableHash(terrain.gameObject.transform);

                Texture2D lightmaplight = LightmapSettings.lightmaps[terrain.lightmapIndex].lightmapColor;
                terrainRendererInfo.lightmapIndex = newLightmapsLight.IndexOf(lightmaplight);
                if (terrainRendererInfo.lightmapIndex == -1)
                {
                    terrainRendererInfo.lightmapIndex = newLightmapsLight.Count;
                    newLightmapsLight.Add(lightmaplight);
                }

                if (newLightmapsMode != LightmapsMode.NonDirectional)
                {
                    Texture2D lightmapdir = LightmapSettings.lightmaps[terrain.lightmapIndex].lightmapDir;
                    terrainRendererInfo.lightmapIndex = newLightmapsDir.IndexOf(lightmapdir);
                    if (terrainRendererInfo.lightmapIndex == -1)
                    {
                        terrainRendererInfo.lightmapIndex = newLightmapsDir.Count;
                        newLightmapsDir.Add(lightmapdir);
                    }
                }
                if (LightmapSettings.lightmaps[terrain.lightmapIndex].shadowMask != null)
                {
                    Texture2D lightmapShadow = LightmapSettings.lightmaps[terrain.lightmapIndex].shadowMask;
                    terrainRendererInfo.lightmapIndex = newLightmapsShadow.IndexOf(lightmapShadow);
                    if (terrainRendererInfo.lightmapIndex == -1)
                    {
                        terrainRendererInfo.lightmapIndex = newLightmapsShadow.Count;
                        newLightmapsShadow.Add(lightmapShadow);
                    }
                }
                newRendererInfos.Add(terrainRendererInfo);

                if (Application.isEditor)
                    Debug.Log(messagePrefix + "Terrain lightmap stored in RendererInfo index " + (newRendererInfos.Count - 1));
            }
        }

        var renderers = FindObjectsOfType(typeof(Renderer));

        if (Application.isEditor)
            Debug.Log("stored info for " + renderers.Length + " meshrenderers");

        foreach (Renderer renderer in renderers)
        {
            if (renderer.lightmapIndex != -1 && renderer.lightmapIndex != 65534)
            {
                RendererInfo info = new RendererInfo();
                info.renderer = renderer;
                info.name = renderer.gameObject.name;
                info.meshHash = renderer.gameObject.GetComponent<MeshFilter>().sharedMesh.GetHashCode();
                info.transformHash = GetStableHash(renderer.gameObject.transform);
                info.lightmapScaleOffset = renderer.lightmapScaleOffset;

                Texture2D lightmaplight = LightmapSettings.lightmaps[renderer.lightmapIndex].lightmapColor;
                info.lightmapIndex = newLightmapsLight.IndexOf(lightmaplight);
                if (info.lightmapIndex == -1)
                {
                    info.lightmapIndex = newLightmapsLight.Count;
                    newLightmapsLight.Add(lightmaplight);
                }

                if (newLightmapsMode != LightmapsMode.NonDirectional)
                {
                    Texture2D lightmapdir = LightmapSettings.lightmaps[renderer.lightmapIndex].lightmapDir;
                    info.lightmapIndex = newLightmapsDir.IndexOf(lightmapdir);
                    if (info.lightmapIndex == -1)
                    {
                        newLightmapsDir.Add(lightmapdir);
                    }
                }
                if (LightmapSettings.lightmaps[renderer.lightmapIndex].shadowMask != null)
                {
                    Texture2D lightmapShadow = LightmapSettings.lightmaps[renderer.lightmapIndex].shadowMask;
                    info.lightmapIndex = newLightmapsShadow.IndexOf(lightmapShadow);
                    if (info.lightmapIndex == -1)
                    {
                        newLightmapsShadow.Add(lightmapShadow);
                    }
                }
                newRendererInfos.Add(info);
            }
        }
        */
    }



}
