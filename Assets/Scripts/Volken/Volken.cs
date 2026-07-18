using System;
using Assets.Scripts;
using ModApi.Scenes.Events;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using ModApi.Craft;
using ModApi.Flight.Sim;

public class Volken
{
    public static Volken Instance { get; private set; }

    public const string CloudConfigListName = "PlanetConfigList";

    // === 多实例云层系统 ===
    public List<CloudLayer> layers = new List<CloudLayer>();
    public PlanetConfigList planetConfigList;

    public CloudLayer MainLayer => layers.Count > 0 ? layers[0] : null;
    public IEnumerable<CloudLayer> ActiveLayers => layers.Where(l => l?.config != null && l.config.enabled);

    public CloudRenderer cloudRenderer;
    public FarCameraScript farCam;

    public const string BlueNoisePath = "Assets/Resources/Volken/BlueNoise.png";
    public const string PerlinFullRough = "Assets/Resources/Volken/DragNoise.png";
    public const string PerlinFullSoft = "Assets/Resources/Volken/flareNoise.png";
    public const string PerlinHalfRough = "Assets/Resources/Volken/PerlinHalfRough.png";
    public const string PerlinHalfSoft = "Assets/Resources/Volken/Noise.png";
    public static string GetNoiseMapPath()
    {
        switch (ModSettings.Instance.NoiseMapIndex)
        {
            case 1: return BlueNoisePath;
            case 2: return PerlinFullRough;
            case 3: return PerlinFullSoft;
            case 4: return PerlinHalfRough;
            case 5: return PerlinHalfSoft;
            default: return PerlinFullRough;
        }
    }

    public List<string> _availableConfigs = new List<string>();
    private Shader _cloudShader;
    

    public static void Initialize()
    {
        Instance ??= new Volken();
    }

    private Volken()
    {
        _cloudShader = Mod.Instance.ResourceLoader.LoadAsset<Shader>("Assets/Scripts/Volken/Clouds.shader");
        planetConfigList = PlanetConfigList.LoadFromFile(CloudConfigListName);
        InitializeLayers();
        Game.Instance.SceneManager.SceneLoaded += OnSceneLoaded;
    }

    private void InitializeLayers()
    {
        // Layer 0: Main
        var main = new CloudLayer
        {
            layerIndex = 0, displayName = "Main",
            noise =new CloudNoise(seed: UnityEngine.Random.Range(1, 99999)),
        };
        main.material = new Material(_cloudShader);
        main.config = CloudConfig.CreateDefault();
        main.currentConfigName = "Default";
        main.currentResolutionScale = main.config.resolutionScale;
        main.runningOffset = main.config.offset;
        layers.Add(main);
        
        var extra1 = new CloudLayer
        {
            layerIndex = 1, displayName = "Extra 1",
            noise = new CloudNoise(seed: UnityEngine.Random.Range(1, 99999)),
        };
        extra1.material = new Material(_cloudShader);
        extra1.config = CreateExtraDefaultConfig();
        extra1.currentConfigName = "ExtraDefault";
        extra1.currentResolutionScale = extra1.config.resolutionScale;
        extra1.runningOffset = extra1.config.offset;
        layers.Add(extra1);

        //add more Rxtra here
        foreach (var layer in layers)
        {
            layer.GenerateNoiseTextures();
            layer.SetStaticShaderProperties();
        }
    }

    private static CloudConfig CreateExtraDefaultConfig()
    {
        return new CloudConfig
        {
            compositeMode = CompositeMode.Additive,
            enabled = false,
            density = 0.01f, absorption = 0.3f, ambientLight = 0.1f, coverage = 0.1f,
            shapeScale = 15000f, detailScale = 10000f, detailStrength = 0.5f,
            phaseParameters = new Vector4(0.75f, -0.75f, 0.5f, 0.5f),
            offset = new Vector3(0.3f, 0.6f, 0.2f),
            windSpeed = 0.0005f, windDirection = 45f, globalRotationAngular = 0.03f,
            scatterStrength = 0.1f, atmoBlendFactor = 1.0f,
            cloudColor = new Color(0.9f, 0.9f, 1.0f, 1f),
            layerHeights = new Vector4(15000f, 25000f, 0f, 0f),
            layerSpreads = new Vector4(5000f, 8000f, 1f, 1f),
            layerStrengths = new Vector4(1.0f, 1.5f, 0f, 0f),
            maxCloudHeight = 35000f, resolutionScale = 0.3f,
            stepSize = 400f, stepSizeFalloff = 1.0f, numLightSamplePoints = 10,
            blueNoiseStrength = 0f, depthThreshold = 0.5f,
            historyBlend = 0f, historyDepthThreshold = 0.05f,
            scatterPower = 1.5f, multiScatterBlend = 0.1f, ambientScatterStrength = 0.3f,
            customWavelengths = new Vector3(680f, 550f, 450f),
            silverLiningIntensity = 1.0f, forwardScatteringBias = 0.7f,
            nearThreshold = 100000f,
        };
    }

    public void AddConfig(string cfg)
    {
        _availableConfigs.Add(cfg);
        Mod.LOG($"Volken: Added config {cfg}, now has {_availableConfigs.Count} configs");
    }

    public void RefreshConfigList()
    {
        Mod.LOG("Refreshing config list");
        try
        {
            _availableConfigs = CloudConfig.GetAllConfigNames(Game.Instance.FlightScene.CraftNode.Parent.Name);
            if (_availableConfigs.Count == 0) _availableConfigs.Add("Default");
            var nm = MainLayer?.currentConfigName ?? "Default";
            if (!_availableConfigs.Contains(nm)) _availableConfigs.Add(nm);
        }
        catch (Exception ex)
        {
            Mod.LOG("Volken: Error refreshing config list: " + ex);
            _availableConfigs = new List<string> { "Default" };
        }
    }

    public void OnFlightSceneLoaded() => OnSceneLoaded(new object(), new SceneEventArgs("Flight"));

    private void OnSceneLoaded(object sender, SceneEventArgs e)
    {
        RefreshConfigList();
        planetConfigList = PlanetConfigList.LoadFromFile(CloudConfigListName);
        if (e.Scene != "Flight") return;

        var main = MainLayer;
        if (main == null) return;

        string planet = Game.Instance.FlightScene.CraftNode.Parent.Name;
        if (_availableConfigs.Count > 0)
        {
            if (!planetConfigList.ExistsInConfig(planet))
            { main.currentConfigName = _availableConfigs[0]; planetConfigList.AddConfig(planet, main.currentConfigName); }
            else main.currentConfigName = planetConfigList.GetConfigName(planet, 0);
            main.config = CloudConfig.LoadFromFile(planet, main.currentConfigName);
        }
        else
        {
            main.currentConfigName = "Default";
            main.config = CloudConfig.CreateDefault();
            main.config.SaveToFile(planet, main.currentConfigName);
            _availableConfigs.Add(main.currentConfigName);
        }

        // Load Extra layer config (remembered per-planet)
        var extra = layers.Count > 1 ? layers[1] : null;
        if (extra != null)
        {
            string extraCfgName = planetConfigList.ExistsInConfig(planet)
                ? planetConfigList.GetConfigName(planet, 1) : null;
            if (!string.IsNullOrEmpty(extraCfgName) && extraCfgName != extra.currentConfigName)
            {
                try
                {
                    var loaded = CloudConfig.LoadFromFile(planet, extraCfgName);
                    extra.config.CopyFrom(loaded);
                    extra.currentConfigName = extraCfgName;
                }
                catch (Exception ex) { Mod.LOG("Volken: Error loading extra config: " + ex); }
            }
        }

        Game.Instance.FlightScene.PlayerChangedSoi += OnPlayerChangedSoi;

        // Apply atmosphere-based enable/disable to ALL layers
        bool hasAtmo = Game.Instance.FlightScene.CraftNode.Parent.PlanetData.AtmosphereData.HasPhysicsAtmosphere;
        foreach (var layer in layers)
        {
            if (layer?.config != null)
                layer.config.enabled = hasAtmo && layer.config.enabled;
        }

        var gameCam = Game.Instance.FlightScene.ViewManager.GameView.GameCamera;
        cloudRenderer = gameCam.NearCamera.gameObject.GetComponent<CloudRenderer>() == null
            ? gameCam.NearCamera.gameObject.AddComponent<CloudRenderer>()
            : gameCam.NearCamera.gameObject.GetComponent<CloudRenderer>();
        farCam = gameCam.FarCamera.gameObject.GetComponent<FarCameraScript>() == null
            ? gameCam.FarCamera.gameObject.AddComponent<FarCameraScript>()
            : gameCam.FarCamera.gameObject.GetComponent<FarCameraScript>();

        Mod.Instance.forceSettingScriptLoadGameObject.SetActive(
            Game.Instance.FlightScene.CraftNode.Parent.PlanetData.HasWater);
    }

    public void OnPlayerChangedSoi(ICraftNode craftNode, IOrbitNode orbitNode)
    {
        if (craftNode.Parent.Parent == null)
        {
            foreach (var l in layers) { if (l?.config != null) l.config.enabled = false; }
            return;
        }

        if (craftNode.Parent.PlanetData.AtmosphereData.HasPhysicsAtmosphere)
        {
            RefreshConfigList();
            string planet = Game.Instance.FlightScene.CraftNode.Parent.Name;
            var main = MainLayer;
            if (main == null) return;

            if (_availableConfigs.Count > 0)
            {
                if (!planetConfigList.ExistsInConfig(planet))
                { main.currentConfigName = _availableConfigs[0]; planetConfigList.AddConfig(planet, main.currentConfigName); }
                else
                {
                    main.currentConfigName = planetConfigList.GetConfigName(planet, 0);
                    if (!_availableConfigs.Contains(main.currentConfigName))
                    { main.currentConfigName = _availableConfigs[0]; planetConfigList.SetConfig(planet, main.currentConfigName, 0); }
                }
                main.config = CloudConfig.LoadFromFile(planet, main.currentConfigName);
            }
            else
            {
                main.currentConfigName = "Default";
                main.config = CloudConfig.CreateDefault();
                main.config.SaveToFile(planet, main.currentConfigName);
                _availableConfigs.Add(main.currentConfigName);
                if (!planetConfigList.ExistsInConfig(planet)) planetConfigList.AddConfig(planet, main.currentConfigName);
                else planetConfigList.SetConfig(planet, main.currentConfigName, 0);
            }

            // Load Extra layer config for this planet (before atmosphere check)
            var extra = layers.Count > 1 ? layers[1] : null;
            if (extra != null)
            {
                string extraCfgName = planetConfigList.ExistsInConfig(planet)
                    ? planetConfigList.GetConfigName(planet, 1) : null;
                if (!string.IsNullOrEmpty(extraCfgName))
                {
                    try
                    {
                        var loaded = CloudConfig.LoadFromFile(planet, extraCfgName);
                        extra.config.CopyFrom(loaded);
                        extra.currentConfigName = extraCfgName;
                    }
                    catch (Exception ex) { Mod.LOG("Volken: Error loading extra config: " + ex); }
                }
            }

            // Apply atmosphere-based enable to ALL layers (respecting loaded config.enabled)
            bool hasAtmo = Game.Instance.FlightScene.CraftNode.Parent.PlanetData.AtmosphereData.HasPhysicsAtmosphere;
            foreach (var layer in layers)
            {
                if (layer?.config != null)
                    layer.config.enabled = hasAtmo && layer.config.enabled;
            }

            VolkenUserInterface.Instance?.RebuildInspectorPanel();
            var gameCam = Game.Instance.FlightScene.ViewManager.GameView.GameCamera;
            cloudRenderer = gameCam.NearCamera.gameObject.GetComponent<CloudRenderer>() == null
                ? gameCam.NearCamera.gameObject.AddComponent<CloudRenderer>()
                : gameCam.NearCamera.gameObject.GetComponent<CloudRenderer>();
            farCam = gameCam.FarCamera.gameObject.GetComponent<FarCameraScript>() == null
                ? gameCam.FarCamera.gameObject.AddComponent<FarCameraScript>()
                : gameCam.FarCamera.gameObject.GetComponent<FarCameraScript>();

            Mod.Instance.forceSettingScriptLoadGameObject.SetActive(
                Game.Instance.FlightScene.CraftNode.Parent.PlanetData.HasWater);
        }
        else
        {
            foreach (var l in layers) { if (l?.config != null) l.config.enabled = false; }
            if (MainLayer != null) MainLayer.currentConfigName = "Default";
        }
    }

    public void ValueChanged()
    {
        cloudRenderer?.SetAllLayersShaderProperties();
    }
}
