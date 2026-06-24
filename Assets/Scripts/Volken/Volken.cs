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
    public const string CloudConfigListName="PlanetConfigList";

    public Material mat;
    public CloudRenderer cloudRenderer;
    public FarCameraScript farCam;

    public RenderTexture whorleyTex;
    public RenderTexture whorleyDetailTex;
    public Texture2D planetMapTex;
    public Texture2D blueNoiseTex;

    private CloudNoise _noise;
    public List<string> _availableConfigs=new List<string>();
    public PlanetConfigList planetConfigList;
    
    public const string BlueNoisePath = "Assets/Resources/Volken/BlueNoise.png";
    public const string PerlinFullRough = "Assets/Resources/Volken/DragNoise.png";
    public const string PerlinFullSoft = "Assets/Resources/Volken/flareNoise.png";
    public const string PerlinHalfRough = "Assets/Resources/Volken/PerlinHalfRough.png";
    public const string PerlinHalfSoft = "Assets/Resources/Volken/Noise.png";

    

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
        
        mat = new Material(Mod.Instance.ResourceLoader.LoadAsset<Shader>("Assets/Scripts/Volken/Clouds.shader"));
        planetConfigList = PlanetConfigList.LoadFromFile(CloudConfigListName);
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
            this._availableConfigs = CloudConfig.GetAllConfigNames(Game.Instance.FlightScene.CraftNode.Parent.Name);
            Mod.LOG($"{CloudConfig.GetAllConfigNames(Game.Instance.FlightScene.CraftNode.Parent.Name).Count}");
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

    public void OnFlightSceneLoaded() => OnSceneLoaded(new object(), new SceneEventArgs("Flight"));

    private void OnSceneLoaded(object sender, SceneEventArgs e)
    {
        RefreshConfigList();
        planetConfigList = PlanetConfigList.LoadFromFile(CloudConfigListName);
        if (e.Scene == "Flight")
        {
            
            if (_availableConfigs.Count > 0)
            {
                if (!planetConfigList.ExistsInConfig(Game.Instance.FlightScene.CraftNode.Parent.Name))
                {
                    currentConfigName = _availableConfigs[0];
                    planetConfigList.AddConfig(Game.Instance.FlightScene.CraftNode.Parent.Name,currentConfigName);
                }
                else
                {
                    currentConfigName = planetConfigList.GetConfigName(Game.Instance.FlightScene.CraftNode.Parent.Name);
                }
                
                cloudConfig = CloudConfig.LoadFromFile(Game.Instance.FlightScene.CraftNode.Parent.Name,currentConfigName);
            }
            else
            {
                currentConfigName = "Default";
                cloudConfig = CloudConfig.CreateDefault();
                cloudConfig.SaveToFile(Game.Instance.FlightScene.CraftNode.Parent.Name,currentConfigName);
                _availableConfigs.Add(currentConfigName);
            }
            Game.Instance.FlightScene.PlayerChangedSoi += OnPlayerChangedSoi;
            bool beforeCgh = cloudConfig.enabled;
            cloudConfig.enabled = false;
            cloudConfig.enabled = Game.Instance.FlightScene.CraftNode.Parent.PlanetData.AtmosphereData.HasPhysicsAtmosphere&&beforeCgh;
            var gameCam = Game.Instance.FlightScene.ViewManager.GameView.GameCamera;
            cloudRenderer = gameCam.NearCamera.gameObject.GetComponent<CloudRenderer>() == null ? gameCam.NearCamera.gameObject.AddComponent<CloudRenderer>() : gameCam.NearCamera.gameObject.GetComponent<CloudRenderer>();
            farCam = gameCam.FarCamera.gameObject.GetComponent<FarCameraScript>() == null ? gameCam.FarCamera.gameObject.AddComponent<FarCameraScript>() : gameCam.FarCamera.gameObject.GetComponent<FarCameraScript>();

            Mod.Instance.forceSettingScriptLoadGameObject.SetActive(Game.Instance.FlightScene.CraftNode.Parent.PlanetData.HasWater);
        }
        else
        {
            try
            {
                Game.Instance.FlightScene.PlayerChangedSoi -= OnPlayerChangedSoi;
            }
            catch (Exception exception)
            {
                Mod.LOG("failed to unregister");
            }
        }
    }
    public void OnPlayerChangedSoi(ICraftNode craftNode, IOrbitNode orbitNode)
    {
        
        if (craftNode.Parent.Parent==null)
        {
            Instance.cloudConfig.enabled = false;
            //dude,it's stupid to give sun cloud
            return;
        }
        
        if (craftNode.Parent.PlanetData.AtmosphereData.HasPhysicsAtmosphere)
        {
            RefreshConfigList();
            string planetName = Game.Instance.FlightScene.CraftNode.Parent.Name;

            if (_availableConfigs.Count > 0)
            {
                if (!planetConfigList.ExistsInConfig(planetName))
                {
                    currentConfigName = _availableConfigs[0];
                    planetConfigList.AddConfig(planetName, currentConfigName);
                }
                else
                {
                    currentConfigName = planetConfigList.GetConfigName(planetName);
                    if (!_availableConfigs.Contains(currentConfigName))
                    {
                        currentConfigName = _availableConfigs[0];
                        planetConfigList.SetConfig(planetName, currentConfigName);
                    }
                }
                cloudConfig = CloudConfig.LoadFromFile(planetName, currentConfigName);
            }
            else
            {
                currentConfigName = "Default";
                cloudConfig = CloudConfig.CreateDefault();
                cloudConfig.SaveToFile(planetName, currentConfigName);
                _availableConfigs.Add(currentConfigName);
                if (!planetConfigList.ExistsInConfig(planetName))
                {
                    planetConfigList.AddConfig(planetName, currentConfigName);
                }
                else
                {
                    planetConfigList.SetConfig(planetName, currentConfigName);
                }
            }
            
            cloudConfig.enabled = false;
            cloudConfig.enabled = Game.Instance.FlightScene.CraftNode.Parent.PlanetData.AtmosphereData.HasPhysicsAtmosphere;
            
            VolkenUserInterface.Instance.RebuildInspectorPanel();
            var gameCam = Game.Instance.FlightScene.ViewManager.GameView.GameCamera;
            if (gameCam.NearCamera.gameObject.GetComponent<CloudRenderer>() == null)
            {
                cloudRenderer = gameCam.NearCamera.gameObject.AddComponent<CloudRenderer>();
            }
            else
            {
                cloudRenderer = gameCam.NearCamera.gameObject.GetComponent<CloudRenderer>();
            }

            if (gameCam.FarCamera.gameObject.GetComponent<FarCameraScript>() == null)
            {
                farCam = gameCam.FarCamera.gameObject.AddComponent<FarCameraScript>();
            }
            else
            {
                farCam = gameCam.FarCamera.gameObject.GetComponent<FarCameraScript>();
            }

            Mod.Instance.forceSettingScriptLoadGameObject.SetActive(Game.Instance.FlightScene.CraftNode.Parent.PlanetData.HasWater);
        }
        else
        {
            cloudConfig.enabled = false;
            currentConfigName="Default";
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