using System;
using Assets.Scripts;
using ModApi.Scenes.Events;
using UnityEngine;
using System.Collections.Generic;
using ModApi.Craft;
using ModApi.Flight.Sim;

public class Volken
{
    public static Volken Instance { get; private set; }

    public CloudConfig cloudConfig;
    public string currentConfigName = "Default";

    public Material mat;
    public CloudRenderer cloudRenderer;
    public FarCameraScript farCam;

    public RenderTexture whorleyTex;
    public RenderTexture whorleyDetailTex;
    public Texture2D planetMapTex;
    public Texture2D blueNoiseTex;

    private CloudNoise _noise;
    public List<string> _availableConfigs = new List<string>();
    
    public const string BlueNoisePath = "Assets/Resources/Volken/BlueNoise.png";
    public const string PerlinFullRough = "Assets/Resources/Volken/PerlinFullRough.png";
    public const string PerlinFullSoft = "Assets/Resources/Volken/PerlinFullSoft.png";
    public const string PerlinHalfRough = "Assets/Resources/Volken/PerlinHalfRough.png";
    public const string PerlinHalfSoft = "Assets/Resources/Volken/PerlinHalfSoft.png";

    private string GetNoiseMapPath()
    {
        switch (ModSettings.Instance.NoiseMapIndex)
        {
            case 1:
                return BlueNoisePath;
            case 2:
                return PerlinFullRough;
            case 3:
                return PerlinFullSoft;
            case 4:
                return PerlinHalfRough;
            case 5:
                return PerlinHalfSoft;
            default:
                return PerlinFullRough;
        }
    }

    public static void Initialize()
    {
        Instance ??= new Volken();
    }

    private Volken()
    {
        if (_availableConfigs.Count > 0)
        {
            //the mod will try load the first config on the list anyway, how the hell do i suppose to know which config you guys want to load?
            currentConfigName = _availableConfigs[0];
            cloudConfig = CloudConfig.LoadFromFile(currentConfigName);
        }
        else
        {
            currentConfigName = "Default";
            cloudConfig = CloudConfig.CreateDefault();
            cloudConfig.SaveToFile(currentConfigName);
            _availableConfigs.Add(currentConfigName);
        }
        
        mat = new Material(Mod.Instance.ResourceLoader.LoadAsset<Shader>("Assets/Scripts/Volken/Clouds.shader"));
        
        _noise = new CloudNoise();
        GenerateNoiseTextures();

        Game.Instance.SceneManager.SceneLoaded += OnSceneLoaded;
    }

    public void AddConfig(string cfg)
    {
        this._availableConfigs.Add(cfg);
        Mod.LOG($"Volken: Added config {cfg} ,now has {this._availableConfigs.Count} configs");
    }

    public void RefreshConfigList()
    {
        Mod.LOG("Refreshing config list");
        try
        {
            this._availableConfigs = CloudConfig.GetAllConfigNames();
            Mod.LOG($"{CloudConfig.GetAllConfigNames().Count}");
            Mod.LOG($"{this._availableConfigs.Count}");
            
            if (this._availableConfigs.Count == 0)
            {
                this._availableConfigs.Add("Default");
            }
            
            if (!this._availableConfigs.Contains(this.currentConfigName))
            {
                this._availableConfigs.Add(this.currentConfigName);
            }
        }
        catch (Exception ex)
        {
            Mod.LOG("Volken: Error refreshing config list: " + ex);
            _availableConfigs = new List<string> { "Default" };
        }
    }

    private void OnSceneLoaded(object sender, SceneEventArgs e)
    {
        if (e.Scene == "Flight")
        {
            Game.Instance.FlightScene.PlayerChangedSoi += OnPlayerChangedSoi;
            cloudConfig.enabled = false;
            cloudConfig.enabled = Game.Instance.FlightScene.CraftNode.Parent.PlanetData.AtmosphereData.HasPhysicsAtmosphere;
            var gameCam = Game.Instance.FlightScene.ViewManager.GameView.GameCamera;
            cloudRenderer = gameCam.NearCamera.gameObject.AddComponent<CloudRenderer>();
            farCam = gameCam.FarCamera.gameObject.AddComponent<FarCameraScript>();
        }
        else
        {
            Game.Instance.FlightScene.PlayerChangedSoi -= OnPlayerChangedSoi;
        }
    }
    private void OnPlayerChangedSoi(ICraftNode craftNode, IOrbitNode orbitNode)
    {
        if (craftNode.Parent.PlanetData.AtmosphereData.HasPhysicsAtmosphere)
        {
            cloudConfig.enabled = false;
            cloudConfig.enabled = Game.Instance.FlightScene.CraftNode.Parent.PlanetData.AtmosphereData.HasPhysicsAtmosphere;
            var gameCam = Game.Instance.FlightScene.ViewManager.GameView.GameCamera;
            cloudRenderer = gameCam.NearCamera.gameObject.AddComponent<CloudRenderer>();
            farCam = gameCam.FarCamera.gameObject.AddComponent<FarCameraScript>();
        }
    }
    private void GenerateNoiseTextures()
    {
        whorleyTex = _noise.GetWhorleyFBM3D(128, 4, 4, 0.5f, 2.0f);
        mat.SetTexture("CloudShapeTex", whorleyTex);
        
        whorleyDetailTex = _noise.GetWhorleyFBM3D(128, 8, 4, 0.5f, 2.0f);
        mat.SetTexture("CloudDetailTex", whorleyDetailTex);

        planetMapTex = _noise.GetPlanetMap(2048, 16.0f, 6, 0.5f, 2.0f);
        mat.SetTexture("PlanetMapTex", planetMapTex);
        
        //blueNoiseTex = Mod.Instance.ResourceLoader.LoadAsset<Texture2D>("Assets/Resources/Volken/PerlinFullRough.png");
        blueNoiseTex = Mod.Instance.ResourceLoader.LoadAsset<Texture2D>(GetNoiseMapPath());
        mat.SetTexture("BlueNoiseTex", blueNoiseTex);
    }
    public void ValueChanged()
    {
        if(cloudRenderer != null) 
        {
            cloudRenderer.SetShaderProperties();
        }
    }
}
