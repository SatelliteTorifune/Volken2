using System;
using System.Linq;
using System.Xml.Linq;
using Assets.Scripts;
using ModApi;
using ModApi.Craft;
using ModApi.Flight.Sim;
using ModApi.Scenes.Events;
using ModApi.Ui;
using ModApi.Ui.Inspector;
using UnityEngine;

public class VolkenUserInterface:MonoBehaviour
{
    public static VolkenUserInterface Instance;

    public const string volkenUserInterfaceID = "toggle-volken-ui-buttom";
    private IInspectorPanel inspectorPanel;
    private InspectorModel inspectorModel;
    
    private void Awake()
    {
        Instance = this;
        DontDestroyOnLoad(this);
    }

    private void Start()
    {
        Game.Instance.SceneManager.SceneLoaded += OnSceneLoaded;
        Game.Instance.UserInterface.AddBuildUserInterfaceXmlAction(UserInterfaceIds.Flight.NavPanel, OnBuildFlightUI);
    }

    private void OnSceneLoaded(object sender, SceneEventArgs e)
    {
        if (e.Scene == "Flight")
        {
            try
            {
                // Ensure Volken is initialized
                Volken.Initialize();
                
                // Refresh config list before creating UI
                Volken.Instance.RefreshConfigList();
                
                CreateInspectorPanel();
                if (inspectorPanel != null)
                {
                    inspectorPanel.Visible = false;
                    inspectorPanel.CloseButtonClicked += OnCloseButtonClicked;
                }
                
                Game.Instance.FlightScene.PlayerChangedSoi += OnPlayerChangedSoi;
                
            }
            catch (Exception ex)
            {
                Mod.LOG("Volken: Error OnSceneLoaded: " + ex);
            }
        }
        else
        {
            try
            {
                Game.Instance.FlightScene.PlayerChangedSoi -= OnPlayerChangedSoi;
            }
            catch (Exception exception)
            {
                Mod.LOG("Volken: Error OnSceneLoaded: " + exception);
            }
        }
    }

    private void OnPlayerChangedSoi(ICraftNode craftNode, IOrbitNode orbitNode)
    {
        try
        {
            if (craftNode.Parent.Parent==null)
            {
                Volken.Instance.cloudConfig.enabled = false;
                return;
            }
            
            if (craftNode?.Parent?.PlanetData?.AtmosphereData != null)
            {
                
                bool hasAtmosphere = craftNode.Parent.PlanetData.AtmosphereData.HasPhysicsAtmosphere;
                Volken.Instance.cloudConfig.enabled = hasAtmosphere;
                
                var gameCam = Game.Instance.FlightScene.ViewManager.GameView.GameCamera;
                if (gameCam != null)
                {
                    if (gameCam.NearCamera != null && Volken.Instance.cloudRenderer == null)
                    {
                        Volken.Instance.cloudRenderer = gameCam.NearCamera.gameObject.AddComponent<CloudRenderer>();
                    }
                    if (gameCam.FarCamera != null && Volken.Instance.farCam == null)
                    {
                        Volken.Instance.farCam = gameCam.FarCamera.gameObject.AddComponent<FarCameraScript>();
                    }
                }
                Volken.Instance.RefreshConfigList();
                RebuildInspectorPanel();
                Volken.Instance.ValueChanged();
                Volken.Instance.OnPlayerChangedSoi(craftNode, orbitNode);
                
            }
            else
            {
                Volken.Instance.RefreshConfigList();
                RebuildInspectorPanel();
                Volken.Instance.ValueChanged();
                Volken.Instance.OnPlayerChangedSoi(craftNode, orbitNode);
            }
            
        }
        catch (Exception ex)
        {
            Mod.LOG("Volken: Error in OnPlayerChangedSoi: " + ex);
        }
    }
    
    private void OnCloseButtonClicked(IInspectorPanel panel)
    {
        if (panel != null)
        {
            panel.Visible = false;
        }
    }

    private static void OnBuildFlightUI(BuildUserInterfaceXmlRequest request)
    {
        try
        {
            var ns = XmlLayoutConstants.XmlNamespace;
            var inspectButton = request.XmlDocument
                .Descendants(ns + "ContentButton")
                .FirstOrDefault(x => (string)x.Attribute("id") == "toggle-flight-inspector");
                
            if (inspectButton != null && inspectButton.Parent != null)
            {
            inspectButton.Parent.Add(
                new XElement(
                    ns + "ContentButton",
                    new XAttribute("id", volkenUserInterfaceID),
                    new XAttribute("class", "panel-button audio-btn-click"),
                    new XAttribute("tooltip", Locale.GetString("Volken.UI.CloudSettings")),
                        new XAttribute("name", "NavPanel.OnToggleVolkenUI"),
                        new XElement(
                            ns + "Image",
                            new XAttribute("class", "panel-button-icon"),
                            new XAttribute("sprite", "Volken/Sprites/VolkenUI"))));
            }
        }
        catch (Exception ex)
        {
            Mod.LOG("Volken: Error building flight UI: " + ex);
        }
    }
    
    public void OnToggleVolkenUI()
    {
        try 
        {
            Volken.Instance.RefreshConfigList();
            if (inspectorPanel == null)
            {
                CreateInspectorPanel();
            }
            if (inspectorPanel != null)
            {
                inspectorPanel.Visible = !inspectorPanel.Visible;
            }
        } 
        catch (Exception ex) 
        {
            Mod.LOG("Volken: Error toggling UI: " + ex);
            try
            {
                CreateInspectorPanel();
                if (inspectorPanel != null)
                {
                    inspectorPanel.Visible = true;
                }
            }
            catch (Exception createEx)
            {
                Mod.LOG("Volken: Error creating panel: " + createEx);
            }
        }
    }
    
    private void CreateInspectorPanel()
    {
        try
        {
            if (inspectorPanel != null)
            {
                try
                {
                    inspectorPanel.CloseButtonClicked -= OnCloseButtonClicked;
                    inspectorPanel.Visible = false;
                }
                catch(Exception e)
                {
                    Mod.LOG($"error in VolkenInterface.CreateInspectorPanel {e}");
                }
            }

            inspectorModel = new InspectorModel("VolkenSettingsInspector", "<color=green>" + Locale.GetString("Volken.UI.CloudSettings") + "</color>");

            #region configManagementGroup
            GroupModel configManagementGroup = new GroupModel(Locale.GetString("Volken.UI.ConfigManagement"));
            
            var currentConfigLabel = new TextModel(Locale.GetString("Volken.UI.CurrentConfig"), () => Volken.Instance.currentConfigName);
            configManagementGroup.Add(currentConfigLabel);
        
            var saveCurrentButton = new TextButtonModel(Locale.GetString("Volken.UI.SaveCurrentConfig"), (Action<TextButtonModel>)(b => 
            {
                try
                {
                    Volken.Instance.cloudConfig.SaveToFile(Game.Instance.FlightScene.CraftNode.Parent.Name,Volken.Instance.currentConfigName);
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(string.Format(Locale.GetString("Volken.UI.ConfigSaved"), Volken.Instance.currentConfigName));
                }
                catch (Exception ex)
                {
                    Mod.LOG("Volken: Error saving config: " + ex);
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.ErrorSavingConfig"));
                }
            }));
            configManagementGroup.Add(saveCurrentButton);
        
            var savePlanetAsButton = new TextButtonModel(Locale.GetString("Volken.UI.SaveAsNewConfig"), (Action<TextButtonModel>)(b => 
            {
                try
                {
                    var dialog = Game.Instance.UserInterface.CreateInputDialog();
                    dialog.MessageText = Locale.GetString("Volken.UI.EnterNewConfigName");
                    dialog.InputText = Locale.GetString("Volken.UI.DefaultConfigName");
                    dialog.OkayClicked += (inputDialog) =>
                    {
                        try
                        {
                            string name = inputDialog.InputText;
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                Volken.Instance.cloudConfig.SaveToFile(Game.Instance.FlightScene.CraftNode.Parent.Name,name);
                                Volken.Instance.currentConfigName = name;
                                Volken.Instance.AddConfig(name);
                                if (Volken.Instance.planetConfigList.ExistsInConfig(Game.Instance.FlightScene.CraftNode.Parent.Name))
                                {
                                    Volken.Instance.planetConfigList.SetConfig(Game.Instance.FlightScene.CraftNode.Parent.Name, name);
                                }
                                else
                                {
                                    Volken.Instance.planetConfigList.AddConfig(Game.Instance.FlightScene.CraftNode.Parent.Name, name);
                                }
                                Volken.Instance.RefreshConfigList();
                                inspectorPanel.Visible = false;
                                RebuildInspectorPanel();
                                Game.Instance.FlightScene.FlightSceneUI.ShowMessage(string.Format(Locale.GetString("Volken.UI.ConfigSavedAs"), Game.Instance.FlightScene.CraftNode.Parent.Name, name));
                            }
                        }
                        catch (Exception ex)
                        {
                            Mod.LOG("Volken: Error saving new config: " + ex);
                            Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.ErrorSavingNewConfig"));
                        }
                        finally
                        {
                            inputDialog?.Close();
                        }
                    };
                }
                catch (Exception ex)
                {
                    Mod.LOG("Volken: Error creating save dialog: " + ex);
                }
            }));
            
            configManagementGroup.Add(savePlanetAsButton);
            
            var loadConfigDropdown = new DropdownModel
            (
                Locale.GetString("Volken.UI.LoadConfig"),
                () => Volken.Instance.currentConfigName,
                (newConfig) =>
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(newConfig) && newConfig != Volken.Instance.currentConfigName)
                        {
                            var loadedConfig = CloudConfig.LoadFromFile(Game.Instance.FlightScene.CraftNode.Parent.Name,newConfig);
                            Volken.Instance.cloudConfig.CopyFrom(loadedConfig);
                            Volken.Instance.currentConfigName = newConfig;
                            Volken.Instance.ValueChanged();
                            
                            if (Volken.Instance.planetConfigList.ExistsInConfig(Game.Instance.FlightScene.CraftNode.Parent.Name))
                            {
                                Volken.Instance.planetConfigList.SetConfig(Game.Instance.FlightScene.CraftNode.Parent.Name,Volken.Instance.currentConfigName);
                            }
                            else
                            {
                                Volken.Instance.planetConfigList.AddConfig(Game.Instance.FlightScene.CraftNode.Parent.Name,Volken.Instance.currentConfigName);
                            }
                            Game.Instance.FlightScene.FlightSceneUI.ShowMessage(string.Format(Locale.GetString("Volken.UI.ConfigLoaded"), newConfig));
                        }
                    }
                    catch (Exception ex)
                    {
                        Mod.LOG("Volken: Error loading config: " + ex);
                        Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.ErrorLoadingConfig"));
                    }
                },
                
                Volken.Instance._availableConfigs
            );
            configManagementGroup.Add(loadConfigDropdown);
           
            var resetToDefaultButton = new TextButtonModel(Locale.GetString("Volken.UI.ResetCurrentToDefault"), (Action<TextButtonModel>)(b => 
            {
                try
                {
                    Volken.Instance.cloudConfig.CopyFrom(CloudConfig.CreateDefault());
                    Volken.Instance.ValueChanged();
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.ConfigResetToDefaults"));
                }
                catch (Exception ex)
                {
                    Mod.LOG("Volken: Error resetting config: " + ex);
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.ErrorResettingConfig"));
                }
            }));
            configManagementGroup.Add(resetToDefaultButton);
            
            var tryAnotherButton = new TextButtonModel(Locale.GetString("Volken.UI.TryAnotherConfig"), (Action<TextButtonModel>)(b => 
            {
                try
                {
                    Volken.Instance.cloudConfig.CopyFrom(CloudConfig.CreateAnotherDefault());
                    Volken.Instance.ValueChanged();
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.ConfigSetToDefaultII"));
                }
                catch (Exception ex)
                {
                    Mod.LOG("Volken: Error setting config: " + ex);
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.ErrorGettingConfig"));
                }
            }));
            configManagementGroup.Add(tryAnotherButton);
            inspectorModel.Add(configManagementGroup);

           
            #endregion
            #region cloudShapeGroup
            GroupModel cloudShapeGroup = new GroupModel(Locale.GetString("Volken.UI.Clouds"));
            var renderToggleModel = new ToggleModel(Locale.GetString("Volken.UI.MainToggle"), () => Volken.Instance.cloudConfig.enabled, s =>
            {
                
            if (!Game.Instance.FlightScene.CraftNode.Parent.PlanetData.AtmosphereData.HasPhysicsAtmosphere)
            {
                Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.NoCloudsHere"));
                Volken.Instance.cloudConfig.enabled = false;
                return;
            }

            if (Game.Instance.FlightScene.CraftNode.Parent.Parent==null)
            {
                Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.NoStarClouds"));
                Volken.Instance.cloudConfig.enabled = false;
                return;
            }
            Volken.Instance.cloudConfig.enabled = s;
            Volken.Instance.ValueChanged();
            });
            
            cloudShapeGroup.Add(renderToggleModel);
            
            var densityModel = new SliderModel(Locale.GetString("Volken.UI.Density"), () => Volken.Instance.cloudConfig.density, s => { Volken.Instance.cloudConfig.density = s;Volken.Instance.ValueChanged(); }, 0.0001f, 0.05f);
            densityModel.ValueFormatter = (f) => FormatValue(f, 4);
            cloudShapeGroup.Add(densityModel);
            
            var absorptionModel = new SliderModel(Locale.GetString("Volken.UI.Absorption"), () => Volken.Instance.cloudConfig.absorption, s => { Volken.Instance.cloudConfig.absorption = s;Volken.Instance.ValueChanged(); }, 0.0f, 1.0f);
            absorptionModel.ValueFormatter = (f) => FormatValue(f, 2);
            cloudShapeGroup.Add(absorptionModel);
            
            var ambientModel = new SliderModel(Locale.GetString("Volken.UI.AmbientLight"), () => Volken.Instance.cloudConfig.ambientLight, s => { Volken.Instance.cloudConfig.ambientLight = s;Volken.Instance.ValueChanged(); }, 0.0f, 0.5f);
            ambientModel.ValueFormatter = (f) => FormatValue(f, 2);
            cloudShapeGroup.Add(ambientModel);
            
            var coverageModel = new SliderModel(Locale.GetString("Volken.UI.Coverage"), () => Volken.Instance.cloudConfig.coverage, s => { Volken.Instance.cloudConfig.coverage = s;Volken.Instance.ValueChanged(); }, -2.0f, 2.0f);
            coverageModel.ValueFormatter = (f) => FormatValue(f, 2);
            cloudShapeGroup.Add(coverageModel);
            
            var shapeScaleModel = new SliderModel(Locale.GetString("Volken.UI.ShapeScale"), () => Volken.Instance.cloudConfig.shapeScale, s => { Volken.Instance.cloudConfig.shapeScale = s;Volken.Instance.ValueChanged(); }, 1000.0f, 50000.0f);
            shapeScaleModel.ValueFormatter = (f) => FormatValue(f, 0);
            cloudShapeGroup.Add(shapeScaleModel);
            
            var detailScaleModel = new SliderModel(Locale.GetString("Volken.UI.DetailScale"), () => Volken.Instance.cloudConfig.detailScale, s => { Volken.Instance.cloudConfig.detailScale = s;Volken.Instance.ValueChanged(); }, 500.0f, 25000.0f);
            detailScaleModel.ValueFormatter = (f) => FormatValue(f, 0);
            cloudShapeGroup.Add(detailScaleModel);
            
            var detailStrengthModel = new SliderModel(Locale.GetString("Volken.UI.DetailStrength"), () => Volken.Instance.cloudConfig.detailStrength, s => { Volken.Instance.cloudConfig.detailStrength = s;Volken.Instance.ValueChanged(); }, 0.0f, 1.0f);
            detailStrengthModel.ValueFormatter = (f) => FormatValue(f, 2);
            cloudShapeGroup.Add(detailStrengthModel);
            
            var speedModel = new SliderModel(Locale.GetString("Volken.UI.CloudMovementSpeed"), () => Volken.Instance.cloudConfig.windSpeed, s => { Volken.Instance.cloudConfig.windSpeed = s;Volken.Instance.ValueChanged(); }, -0.05f, 0.05f);
            speedModel.ValueFormatter = (f) => FormatValue(f, 4);
            cloudShapeGroup.Add(speedModel);
            
            var windDirectionModel = new SliderModel(Locale.GetString("Volken.UI.WindDirection"), () => Volken.Instance.cloudConfig.windDirection, s => { Volken.Instance.cloudConfig.windDirection = s;Volken.Instance.ValueChanged(); }, 0.0f, 360.0f, true);
            windDirectionModel.ValueFormatter = (f) => FormatValue(f, 0);
            cloudShapeGroup.Add(windDirectionModel);
            
            var globalRotationAngularModel = new SliderModel(Locale.GetString("Volken.UI.GlobalRotationAngular"), () => Volken.Instance.cloudConfig.globalRotationAngular, s => { Volken.Instance.cloudConfig.globalRotationAngular = s;Volken.Instance.ValueChanged(); }, -2.0f, 2.0f);
            globalRotationAngularModel.ValueFormatter = (f) => FormatValue(f, 2);
            cloudShapeGroup.Add(globalRotationAngularModel);
            
            var cloudColorRedModel = new SliderModel(Locale.GetString("Volken.UI.CloudColorRed"), () => Volken.Instance.cloudConfig.cloudColor.r, s => { Volken.Instance.cloudConfig.cloudColor.r = s;Volken.Instance.ValueChanged(); }, 0.0f, 1.0f, false);
            cloudColorRedModel.ValueFormatter = (f) => FormatColorValue(f);
            cloudShapeGroup.Add(cloudColorRedModel);
            
            var cloudColorGreenModel = new SliderModel(Locale.GetString("Volken.UI.CloudColorGreen"), () => Volken.Instance.cloudConfig.cloudColor.g, s => { Volken.Instance.cloudConfig.cloudColor.g = s;Volken.Instance.ValueChanged();}, 0.0f, 1.0f, false);
            cloudColorGreenModel.ValueFormatter = (f) => FormatColorValue(f);
            cloudShapeGroup.Add(cloudColorGreenModel);
            
            var cloudColorBlueModel = new SliderModel(Locale.GetString("Volken.UI.CloudColorBlue"), () => Volken.Instance.cloudConfig.cloudColor.b, s => { Volken.Instance.cloudConfig.cloudColor.b = s;Volken.Instance.ValueChanged();}, 0.0f, 1.0f, false);
            cloudColorBlueModel.ValueFormatter = (f) => FormatColorValue(f);
            cloudShapeGroup.Add(cloudColorBlueModel);
            
            var scatterModel = new SliderModel(Locale.GetString("Volken.UI.ScatterStrength"), () => Volken.Instance.cloudConfig.scatterStrength, s => { Volken.Instance.cloudConfig.scatterStrength = s;Volken.Instance.ValueChanged(); }, 0.0f, 2.0f);
            scatterModel.ValueFormatter = (f) => FormatValue(f, 3);
            cloudShapeGroup.Add(scatterModel);
            
            var atmoBlendModel = new SliderModel(Locale.GetString("Volken.UI.AtmosphereBlendFactor"), () => Volken.Instance.cloudConfig.atmoBlendFactor, s => { Volken.Instance.cloudConfig.atmoBlendFactor = s;Volken.Instance.ValueChanged(); }, 0.0f, 50.0f);
            atmoBlendModel.ValueFormatter = (f) => FormatValue(f, 2);
            cloudShapeGroup.Add(atmoBlendModel);
            
            var nearThresholdModel = new SliderModel(Locale.GetString("Volken.UI.NearThreshold"), () => Volken.Instance.cloudConfig.nearThreshold, s => { Volken.Instance.cloudConfig.nearThreshold = s;Volken.Instance.ValueChanged(); }, 1e3f, 2e4f);
            nearThresholdModel.ValueFormatter = (f) => FormatValue(f, 2);
            //cloudShapeGroup.Add(nearThresholdModel);
            
            var scatterPowerModel = new SliderModel(Locale.GetString("Volken.UI.ScatterPower"), () => Volken.Instance.cloudConfig.scatterPower, s => { Volken.Instance.cloudConfig.scatterPower = s;Volken.Instance.ValueChanged(); }, 1.0f, 2.5f);
            scatterPowerModel.ValueFormatter = (f) => FormatValue(f, 2);
            cloudShapeGroup.Add(scatterPowerModel);
            
            var multiScatterBlendModel = new SliderModel(Locale.GetString("Volken.UI.MultiScatterBlend"), () => Volken.Instance.cloudConfig.multiScatterBlend, s => { Volken.Instance.cloudConfig.multiScatterBlend = s;Volken.Instance.ValueChanged(); }, 0.0f, 1.0f);
            multiScatterBlendModel.ValueFormatter = (f) => FormatValue(f, 2);
            cloudShapeGroup.Add(multiScatterBlendModel);
            
            var ambientScatterStrengthModel = new SliderModel(Locale.GetString("Volken.UI.AmbientScatter"), () => Volken.Instance.cloudConfig.ambientScatterStrength, s => { Volken.Instance.cloudConfig.ambientScatterStrength = s;Volken.Instance.ValueChanged(); }, 0.0f, 2.0f);
            ambientScatterStrengthModel.ValueFormatter = (f) => FormatValue(f, 2);
            cloudShapeGroup.Add(ambientScatterStrengthModel);
            
            var silverLiningModel = new SliderModel(Locale.GetString("Volken.UI.SilverLiningIntensity"), () => Volken.Instance.cloudConfig.silverLiningIntensity, s => { Volken.Instance.cloudConfig.silverLiningIntensity = s;Volken.Instance.ValueChanged(); }, 0.0f, 3.0f);
            silverLiningModel.ValueFormatter = (f) => FormatValue(f, 2);
            cloudShapeGroup.Add(silverLiningModel);
            
            var forwardScatterBiasModel = new SliderModel(Locale.GetString("Volken.UI.ForwardScatterBias"), () => Volken.Instance.cloudConfig.forwardScatteringBias, s => { Volken.Instance.cloudConfig.forwardScatteringBias = s;Volken.Instance.ValueChanged(); }, 0.0f, 0.99f);
            forwardScatterBiasModel.ValueFormatter = (f) => FormatValue(f, 2);
            cloudShapeGroup.Add(forwardScatterBiasModel);
            
            inspectorModel.Add(cloudShapeGroup);
            #endregion
            #region containerSettingsGroup
            GroupModel containerSettingsGroup = new GroupModel(Locale.GetString("Volken.UI.CloudContainer"));
            
            var layer1HeightModel = new SliderModel(Locale.GetString("Volken.UI.Layer1Height"), () => Volken.Instance.cloudConfig.layerHeights.x, s => { Volken.Instance.cloudConfig.layerHeights.x = s;Volken.Instance.ValueChanged(); }, 500.0f, 10000.0f);
            layer1HeightModel.ValueFormatter = (f) => FormatValue(f, 0);
            containerSettingsGroup.Add(layer1HeightModel);
            
            var layer1WidthModel = new SliderModel(Locale.GetString("Volken.UI.Layer1Spread"), () => Volken.Instance.cloudConfig.layerSpreads.x, s => { Volken.Instance.cloudConfig.layerSpreads.x = s;Volken.Instance.ValueChanged(); }, 100.0f, 5000.0f);
            layer1WidthModel.ValueFormatter = (f) => FormatValue(f, 0);
            containerSettingsGroup.Add(layer1WidthModel);
            
            var layer1StrengthModel = new SliderModel(Locale.GetString("Volken.UI.Layer1Strength"), () => Volken.Instance.cloudConfig.layerStrengths.x, s => { Volken.Instance.cloudConfig.layerStrengths.x = s;Volken.Instance.ValueChanged(); }, 0.0f, 2.0f);
            layer1StrengthModel.ValueFormatter = (f) => FormatValue(f, 1);
            containerSettingsGroup.Add(layer1StrengthModel);
            
            var layer2HeightModel = new SliderModel(Locale.GetString("Volken.UI.Layer2Height"), () => Volken.Instance.cloudConfig.layerHeights.y, s => { Volken.Instance.cloudConfig.layerHeights.y = s;Volken.Instance.ValueChanged(); }, 500.0f, 10000.0f);
            layer2HeightModel.ValueFormatter = (f) => FormatValue(f, 0);
            containerSettingsGroup.Add(layer2HeightModel);
            
            var layer2WidthModel = new SliderModel(Locale.GetString("Volken.UI.Layer2Spread"), () => Volken.Instance.cloudConfig.layerSpreads.y, s => { Volken.Instance.cloudConfig.layerSpreads.y = s;Volken.Instance.ValueChanged(); }, 100.0f, 5000.0f);
            layer2WidthModel.ValueFormatter = (f) => FormatValue(f, 0);
            containerSettingsGroup.Add(layer2WidthModel);
            
            var layer2StrengthModel = new SliderModel(Locale.GetString("Volken.UI.Layer2Strength"), () => Volken.Instance.cloudConfig.layerStrengths.y, s => { Volken.Instance.cloudConfig.layerStrengths.y = s;Volken.Instance.ValueChanged(); }, 0.0f, 2.0f);
            layer2StrengthModel.ValueFormatter = (f) => FormatValue(f, 1);
            containerSettingsGroup.Add(layer2StrengthModel);
            
            var maxHeightModel = new SliderModel(Locale.GetString("Volken.UI.MaxCloudHeight"), () => Volken.Instance.cloudConfig.maxCloudHeight, s => { Volken.Instance.cloudConfig.maxCloudHeight = s;Volken.Instance.ValueChanged(); }, 1000.0f, 25000.0f);
            maxHeightModel.ValueFormatter = (f) => FormatValue(f, 0);
            containerSettingsGroup.Add(maxHeightModel);
            inspectorModel.Add(containerSettingsGroup);
            #endregion
            #region qualityGroup
            GroupModel qualityGroup = new GroupModel(Locale.GetString("Volken.UI.CloudQuality"));
            var resolutionScaleModel = new SliderModel(Locale.GetString("Volken.UI.ResolutionScale"), () => Volken.Instance.cloudConfig.resolutionScale, s => { Volken.Instance.cloudConfig.resolutionScale = Mathf.Clamp(s, 0.1f, 1.0f); }, 0.1f, 1.0f);
            resolutionScaleModel.ValueFormatter = (f) => FormatValue(f, 2);
            qualityGroup.Add(resolutionScaleModel);
            
            var stepSizeModel = new SliderModel(Locale.GetString("Volken.UI.StepSize"), () => Volken.Instance.cloudConfig.stepSize, s => { Volken.Instance.cloudConfig.stepSize = s;Volken.Instance.ValueChanged(); }, 100.0f, 2000.0f);
            stepSizeModel.ValueFormatter = (f) => FormatValue(f, 0);
            qualityGroup.Add(stepSizeModel);
            
            var falloffModel = new SliderModel(Locale.GetString("Volken.UI.StepSizeFalloff"), () => Volken.Instance.cloudConfig.stepSizeFalloff, s => { Volken.Instance.cloudConfig.stepSizeFalloff = s;Volken.Instance.ValueChanged(); }, 0.1f, 3.0f);
            falloffModel.ValueFormatter = (f) => FormatValue(f, 2);
            qualityGroup.Add(falloffModel);
            
            var numLightSamplesModel = new SliderModel(Locale.GetString("Volken.UI.NumberOfLightSamples"), () => Volken.Instance.cloudConfig.numLightSamplePoints, s => { Volken.Instance.cloudConfig.numLightSamplePoints = Mathf.RoundToInt(s);Volken.Instance.ValueChanged(); }, 1, 25, true);
            numLightSamplesModel.ValueFormatter = (f) => FormatValue(f, 0);
            qualityGroup.Add(numLightSamplesModel);
            
            var thresholdModel = new SliderModel(Locale.GetString("Volken.UI.Threshold"), () => Volken.Instance.cloudConfig.depthThreshold, s => { Volken.Instance.cloudConfig.depthThreshold = s;Volken.Instance.ValueChanged(); }, 0.0f, 1.0f);
            thresholdModel.ValueFormatter = (f) => FormatValue(f, 2);
            //qualityGroup.Add(thresholdModel);
            
            var rayOffsetStrengthModel = new SliderModel(Locale.GetString("Volken.UI.RayOffsetStrength"), () => Volken.Instance.cloudConfig.blueNoiseStrength, s => { Volken.Instance.cloudConfig.blueNoiseStrength = s;Volken.Instance.ValueChanged(); }, 0.0f, 10.0f);
            rayOffsetStrengthModel.ValueFormatter = (f) => FormatValue(f, 1);
            qualityGroup.Add(rayOffsetStrengthModel);
            
            var historyBlendModel = new SliderModel(Locale.GetString("Volken.UI.HistoryBlend"), () => Volken.Instance.cloudConfig.historyBlend, s => { Volken.Instance.cloudConfig.historyBlend = s;Volken.Instance.ValueChanged(); }, 0.0f, 0.99f);
            historyBlendModel.ValueFormatter = (f) => FormatValue(f, 2);
            qualityGroup.Add(historyBlendModel);
            inspectorModel.Add(qualityGroup);
            #endregion

            // Create the panel
            inspectorPanel = Game.Instance.UserInterface.CreateInspectorPanel(inspectorModel, new InspectorPanelCreationInfo()
            {
                PanelWidth = 400,
                Resizable = true,
            });
            
            if (inspectorPanel != null)
            {
                inspectorPanel.Visible = false;
            }
        }
        catch (Exception ex)
        {
            Mod.LOG("Volken: Error creating inspector panel: " + ex);
            inspectorPanel = null;
        }
    }
    public void RebuildInspectorPanel()
    {
        try
        {
            // 先销毁旧面板
            if (inspectorPanel != null)
            {
                try
                {
                    inspectorPanel.CloseButtonClicked -= OnCloseButtonClicked;
                    inspectorPanel.Visible = false;
                }
                catch { /* 忽略错误 */ }
                inspectorPanel = null;
            }
        
            // 创建新面板
            CreateInspectorPanel();
        }
        catch (Exception ex)
        {
            Mod.LOG("Volken: Error rebuilding panel: " + ex);
        }
    }
    
    private string FormatValue(float arg, int decimals) 
    { 
        return arg.ToString("n" + Mathf.Max(0, decimals)); 
    }
    
    private string FormatColorValue(float arg)
    {
        return ((arg / 1)*255).ToString("N0");
    }
   
    private void OnDestroy()
    {
        if (inspectorPanel != null)
        {
            try
            {
                inspectorPanel.CloseButtonClicked -= OnCloseButtonClicked;
            }
            catch
            {
                
            }
        }
        
      
    }
}
