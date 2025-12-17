using System;
using Assets.Scripts;
using ModApi.Scenes.Events;
using ModApi.Ui.Inspector;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

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
    private List<string> _availableConfigs = new List<string>();
    
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
        RefreshConfigList();
        
        if (_availableConfigs.Count > 0)
        {
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
        Game.Instance.UserInterface.AddBuildInspectorPanelAction(InspectorIds.FlightView, OnBuildFlightViewInspectorPanel);
    }

    private void OnSceneLoaded(object sender, SceneEventArgs e)
    {
        if (e.Scene == "Flight")
        {
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

    private void RefreshConfigList()
    {
        _availableConfigs = CloudConfig.GetAllConfigNames();
        if (_availableConfigs.Count == 0)
        {
            _availableConfigs.Add("Default");
        }
    }

    private void OnBuildFlightViewInspectorPanel(BuildInspectorPanelRequest request)
    {
        
        GroupModel configManagementGroup = new GroupModel("Config Management");
        request.Model.AddGroup(configManagementGroup);
    
        var currentConfigLabel = new TextModel("Current Config", () => currentConfigName);
        configManagementGroup.Add(currentConfigLabel);
    
        var saveCurrentButton = new TextButtonModel("Save Current Config", (Action<TextButtonModel>)(b => 
        {
            cloudConfig.SaveToFile(currentConfigName);
            Game.Instance.FlightScene.FlightSceneUI.ShowMessage($"Config '{currentConfigName}' saved!");
        }));
        configManagementGroup.Add(saveCurrentButton);
    
        var saveAsButton = new TextButtonModel("Save As New Config", (Action<TextButtonModel>)(b => 
        {
            var dialog = Game.Instance.UserInterface.CreateInputDialog();
            dialog.MessageText = "Enter new config name:";
            dialog.InputText = "NewConfig";
            dialog.OkayClicked += (inputDialog) =>
            {
                string name = inputDialog.InputText;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    cloudConfig.SaveToFile(name);
                    currentConfigName = name;
                    RefreshConfigList();
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage($"Config saved as '{name}'!");
                }
                inputDialog.Close();
            };
        }));
        configManagementGroup.Add(saveAsButton);
    
        var loadConfigDropdown = new DropdownModel(
            "Load Config",
            () => currentConfigName,
            (newConfig) =>
            {
                if (!string.IsNullOrWhiteSpace(newConfig) && newConfig != currentConfigName)
                {
                    CloudConfig loadedConfig = CloudConfig.LoadFromFile(newConfig);
                    cloudConfig.CopyFrom(loadedConfig);
                    currentConfigName = newConfig;
                    ValueChanged();
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage($"Config '{newConfig}' loaded!");
                }
            },
            _availableConfigs
        );
        configManagementGroup.Add(loadConfigDropdown);
        
        var resetToDefaultButton = new TextButtonModel("Reset Current to Default", (Action<TextButtonModel>)(b => 
        {
            CloudConfig defaultConfig = CloudConfig.CreateDefault();
            cloudConfig.CopyFrom(defaultConfig);
            ValueChanged();
            Game.Instance.FlightScene.FlightSceneUI.ShowMessage("Config reset to defaults!");
        }));
        configManagementGroup.Add(resetToDefaultButton);


        GroupModel cloudShapeGroup = new GroupModel("Clouds");
        request.Model.AddGroup(cloudShapeGroup);

        var renderToggleModel = new ToggleModel("Main Toggle", () => cloudConfig.enabled, s =>
        {
            cloudConfig.enabled = s;

            if (s && !Game.Instance.FlightScene.CraftNode.Parent.PlanetData.AtmosphereData.HasPhysicsAtmosphere)
                Game.Instance.FlightScene.FlightSceneUI.ShowMessage("°`_´°");

            ValueChanged();
        });

        cloudShapeGroup.Add(renderToggleModel);

        var densityModel = new SliderModel("Density", () => cloudConfig.density, s => { cloudConfig.density = s; ValueChanged(); }, 0.0001f, 0.05f);
        densityModel.ValueFormatter = (f) => FormatValue(f, 4);
        cloudShapeGroup.Add(densityModel);

        var absorptionModel = new SliderModel("Absorption", () => cloudConfig.absorption, s => { cloudConfig.absorption = s; ValueChanged(); }, 0.0f, 1.0f);
        absorptionModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(absorptionModel);

        var ambientModel = new SliderModel("Ambient Light", () => cloudConfig.ambientLight, s => { cloudConfig.ambientLight = s; ValueChanged(); }, 0.0f, 0.5f);
        ambientModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(ambientModel);

        var coverageModel = new SliderModel("Coverage", () => cloudConfig.coverage, s => { cloudConfig.coverage = s; ValueChanged(); }, 0.0f, 1.0f);
        coverageModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(coverageModel);

        var shapeScaleModel = new SliderModel("Shape Scale", () => cloudConfig.shapeScale, s => { cloudConfig.shapeScale = s; ValueChanged(); }, 1000.0f, 50000.0f);
        shapeScaleModel.ValueFormatter = (f) => FormatValue(f, 0);
        cloudShapeGroup.Add(shapeScaleModel);

        var detailScaleModel = new SliderModel("Detail Scale", () => cloudConfig.detailScale, s => { cloudConfig.detailScale = s; ValueChanged(); }, 500.0f, 25000.0f);
        detailScaleModel.ValueFormatter = (f) => FormatValue(f, 0);
        cloudShapeGroup.Add(detailScaleModel);

        var detailStrengthModel = new SliderModel("Detail Strength", () => cloudConfig.detailStrength, s => { cloudConfig.detailStrength = s; ValueChanged(); }, 0.0f, 1.0f);
        detailStrengthModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(detailStrengthModel);

        var speedModel = new SliderModel("Cloud Movement Speed", () => cloudConfig.windSpeed, s => { cloudConfig.windSpeed = s; ValueChanged(); }, -0.05f, 0.05f);
        speedModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(speedModel);

        var windDirectionModel = new SliderModel("Wind Direction", () => cloudConfig.windDirection, s => { cloudConfig.windDirection = s; ValueChanged(); }, 0.0f, 360.0f, true);
        windDirectionModel.ValueFormatter = (f) => FormatValue(f, 0);
        cloudShapeGroup.Add(windDirectionModel);
        
        var globalRotationAngularModel = new SliderModel("Global Rotation Angular", () => cloudConfig.globalRotationAngular, s => { cloudConfig.globalRotationAngular = s; ValueChanged(); }, -2.0f, 2.0f);
        globalRotationAngularModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(globalRotationAngularModel);

        var cloudColorRedModel = new SliderModel("Cloud Color Red", () => cloudConfig.cloudColor.r, s => { cloudConfig.cloudColor.r = s; ValueChanged(); }, 0.0f, 1.0f, false);
        cloudColorRedModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(cloudColorRedModel);
        
        var cloudColorGreenModel = new SliderModel("Cloud Color Green", () => cloudConfig.cloudColor.g, s => { cloudConfig.cloudColor.g = s; ValueChanged();}, 0.0f, 1.0f, false);
        cloudColorGreenModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(cloudColorGreenModel);
        
        var cloudColorBlueModel = new SliderModel("Cloud Color Blue", () => cloudConfig.cloudColor.b, s => { cloudConfig.cloudColor.b = s; ValueChanged();}, 0.0f, 1.0f, false);
        cloudColorBlueModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(cloudColorBlueModel);

        var scatterModel = new SliderModel("Scatter Strength", () => cloudConfig.scatterStrength, s => { cloudConfig.scatterStrength = s; ValueChanged(); }, 0.0f, 5.0f);
        scatterModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(scatterModel);

        var atmoBlendModel = new SliderModel("Atmosphere Blend Factor", () => cloudConfig.atmoBlendFactor, s => { cloudConfig.atmoBlendFactor = s; ValueChanged(); }, 0.0f, 1.0f);
        atmoBlendModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(atmoBlendModel);
        
        var scatterPowerModel = new SliderModel("Scatter Power", () => cloudConfig.scatterPower, s => { cloudConfig.scatterPower = s; ValueChanged(); }, 1.0f, 2.5f);
        scatterPowerModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(scatterPowerModel);

        var multiScatterBlendModel = new SliderModel("Multi-Scatter Blend", () => cloudConfig.multiScatterBlend, s => { cloudConfig.multiScatterBlend = s; ValueChanged(); }, 0.0f, 1.0f);
        multiScatterBlendModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(multiScatterBlendModel);

        var ambientScatterStrengthModel = new SliderModel("Ambient Scatter", () => cloudConfig.ambientScatterStrength, s => { cloudConfig.ambientScatterStrength = s; ValueChanged(); }, 0.0f, 2.0f);
        ambientScatterStrengthModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(ambientScatterStrengthModel);

        var silverLiningModel = new SliderModel("Silver Lining Intensity", () => cloudConfig.silverLiningIntensity, s => { cloudConfig.silverLiningIntensity = s; ValueChanged(); }, 0.0f, 3.0f);
        silverLiningModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(silverLiningModel);

        var forwardScatterBiasModel = new SliderModel("Forward Scatter Bias", () => cloudConfig.forwardScatteringBias, s => { cloudConfig.forwardScatteringBias = s; ValueChanged(); }, 0.0f, 0.99f);
        forwardScatterBiasModel.ValueFormatter = (f) => FormatValue(f, 2);
        cloudShapeGroup.Add(forwardScatterBiasModel);

        GroupModel containerSettingsGroup = new GroupModel("Cloud Container");
        request.Model.AddGroup(containerSettingsGroup);

        var layer1HeightModel = new SliderModel("Layer 1 Height", () => cloudConfig.layerHeights.x, s => { cloudConfig.layerHeights.x = s; ValueChanged(); }, 500.0f, 10000.0f);
        layer1HeightModel.ValueFormatter = (f) => FormatValue(f, 0);
        containerSettingsGroup.Add(layer1HeightModel);

        var layer1WidthModel = new SliderModel("Layer 1 Spread", () => cloudConfig.layerSpreads.x, s => { cloudConfig.layerSpreads.x = s; ValueChanged(); }, 100.0f, 5000.0f);
        layer1WidthModel.ValueFormatter = (f) => FormatValue(f, 0);
        containerSettingsGroup.Add(layer1WidthModel);

        var layer1StrengthModel = new SliderModel("Layer 1 Strength", () => cloudConfig.layerStrengths.x, s => { cloudConfig.layerStrengths.x = s; ValueChanged(); }, 0.0f, 2.0f);
        layer1StrengthModel.ValueFormatter = (f) => FormatValue(f, 1);
        containerSettingsGroup.Add(layer1StrengthModel);

        var layer2HeightModel = new SliderModel("Layer 2 Height", () => cloudConfig.layerHeights.y, s => { cloudConfig.layerHeights.y = s; ValueChanged(); }, 500.0f, 10000.0f);
        layer2HeightModel.ValueFormatter = (f) => FormatValue(f, 0);
        containerSettingsGroup.Add(layer2HeightModel);

        var layer2WidthModel = new SliderModel("Layer 2 Spread", () => cloudConfig.layerSpreads.y, s => { cloudConfig.layerSpreads.y = s; ValueChanged(); }, 100.0f, 5000.0f);
        layer2WidthModel.ValueFormatter = (f) => FormatValue(f, 0);
        containerSettingsGroup.Add(layer2WidthModel);

        var layer2StrengthModel = new SliderModel("Layer 2 Strength", () => cloudConfig.layerStrengths.y, s => { cloudConfig.layerStrengths.y = s; ValueChanged(); }, 0.0f, 2.0f);
        layer2StrengthModel.ValueFormatter = (f) => FormatValue(f, 1);
        containerSettingsGroup.Add(layer2StrengthModel);

        var maxHeightModel = new SliderModel("Max Cloud Height", () => cloudConfig.maxCloudHeight, s => { cloudConfig.maxCloudHeight = s; ValueChanged(); }, 1000.0f, 25000.0f);
        maxHeightModel.ValueFormatter = (f) => FormatValue(f, 0);
        containerSettingsGroup.Add(maxHeightModel);

        GroupModel qualityGroup = new GroupModel("Cloud Quality");
        request.Model.AddGroup(qualityGroup);

        var resolutionScaleModel = new SliderModel("Resolution Scale", () => cloudConfig.resolutionScale, s => { cloudConfig.resolutionScale = Mathf.Clamp(s, 0.1f, 1.0f); }, 0.1f, 1.0f);
        resolutionScaleModel.ValueFormatter = (f) => FormatValue(f, 2);
        qualityGroup.Add(resolutionScaleModel);

        var stepSizeModel = new SliderModel("Step Size", () => cloudConfig.stepSize, s => { cloudConfig.stepSize = s; ValueChanged(); }, 100.0f, 2000.0f);
        stepSizeModel.ValueFormatter = (f) => FormatValue(f, 0);
        qualityGroup.Add(stepSizeModel);

        var falloffModel = new SliderModel("Step Size Falloff", () => cloudConfig.stepSizeFalloff, s => { cloudConfig.stepSizeFalloff = s; ValueChanged(); }, 0.1f, 3.0f);
        falloffModel.ValueFormatter = (f) => FormatValue(f, 2);
        qualityGroup.Add(falloffModel);

        var numLightSamplesModel = new SliderModel("Number of Light Samples", () => cloudConfig.numLightSamplePoints, s => { cloudConfig.numLightSamplePoints = Mathf.RoundToInt(s); ValueChanged(); }, 1, 25, true);
        numLightSamplesModel.ValueFormatter = (f) => FormatValue(f, 0);
        qualityGroup.Add(numLightSamplesModel);

        var thresholdModel = new SliderModel("Threshold", () => cloudConfig.depthThreshold, s => { cloudConfig.depthThreshold = s; ValueChanged(); }, 0.0f, 1.0f);
        thresholdModel.ValueFormatter = (f) => FormatValue(f, 2);
        qualityGroup.Add(thresholdModel);

        var rayOffsetStrengthModel = new SliderModel("Ray Offset Strength", () => cloudConfig.blueNoiseStrength, s => { cloudConfig.blueNoiseStrength = s; ValueChanged(); }, 0.0f, 10.0f);
        rayOffsetStrengthModel.ValueFormatter = (f) => FormatValue(f, 1);
        qualityGroup.Add(rayOffsetStrengthModel);

        var historyBlendModel = new SliderModel("History Blend", () => cloudConfig.historyBlend, s => { cloudConfig.historyBlend = s; ValueChanged(); }, 0.0f, 0.99f);
        historyBlendModel.ValueFormatter = (f) => FormatValue(f, 2);
        qualityGroup.Add(historyBlendModel);
    }

    private void ValueChanged()
    {
        if(cloudRenderer != null) 
        {
            cloudRenderer.SetShaderProperties();
        }
    }

    private string FormatValue(float arg, int decimals) 
    { 
        return arg.ToString("n" + Mathf.Max(0, decimals)); 
    }
}
