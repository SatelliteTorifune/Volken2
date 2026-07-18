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

public class VolkenUserInterface : MonoBehaviour
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
                Volken.Initialize();

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
            var main = Volken.Instance.MainLayer;
            if (main == null) return;

            if (craftNode.Parent.Parent == null)
            {
                main.config.enabled = false;
                return;
            }

            if (craftNode?.Parent?.PlanetData?.AtmosphereData != null)
            {
                bool hasAtmosphere = craftNode.Parent.PlanetData.AtmosphereData.HasPhysicsAtmosphere;
                main.config.enabled = hasAtmosphere;

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
                catch (Exception e)
                {
                    Mod.LOG($"error in VolkenInterface.CreateInspectorPanel {e}");
                }
            }

            inspectorModel = new InspectorModel("VolkenSettingsInspector",
                "<color=green>" + Locale.GetString("Volken.UI.CloudSettings") + "</color>");

            var main = Volken.Instance.MainLayer;
            if (main == null) return;

            // === Config Management (uses MainLayer) ===
            CreateConfigManagementGroup(main);

            // === Main Layer ===
            CreateLayerGroup(main, "Main");

            // === Extra Layers ===
            for (int i = 1; i < Volken.Instance.layers.Count; i++)
            {
                var layer = Volken.Instance.layers[i];
                if (layer != null)
                {
                    CreateExtraConfigManagementGroup(layer, layer.displayName);
                    CreateExtraLayerGroup(layer, layer.displayName);
                }
            }

            // Create the panel
            inspectorPanel = Game.Instance.UserInterface.CreateInspectorPanel(inspectorModel,
                new InspectorPanelCreationInfo()
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

    #region Config Management

    private void CreateConfigManagementGroup(CloudLayer mainLayer)
    {
        GroupModel configManagementGroup = new GroupModel(Locale.GetString("Volken.UI.ConfigManagement"));

        var currentConfigLabel = new TextModel(Locale.GetString("Volken.UI.CurrentConfig"),
            () => mainLayer.currentConfigName);
        configManagementGroup.Add(currentConfigLabel);

        var saveCurrentButton = new TextButtonModel(Locale.GetString("Volken.UI.SaveCurrentConfig"),
            (Action<TextButtonModel>)(b =>
            {
                try
                {
                    mainLayer.config.SaveToFile(
                        Game.Instance.FlightScene.CraftNode.Parent.Name,
                        mainLayer.currentConfigName);
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                        string.Format(Locale.GetString("Volken.UI.ConfigSaved"), mainLayer.currentConfigName));
                }
                catch (Exception ex)
                {
                    Mod.LOG("Volken: Error saving config: " + ex);
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                        Locale.GetString("Volken.UI.ErrorSavingConfig"));
                }
            }));
        configManagementGroup.Add(saveCurrentButton);

        var saveAsButton = new TextButtonModel(Locale.GetString("Volken.UI.SaveAsNewConfig"),
            (Action<TextButtonModel>)(b =>
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
                                mainLayer.config.SaveToFile(
                                    Game.Instance.FlightScene.CraftNode.Parent.Name, name);
                                mainLayer.currentConfigName = name;
                                Volken.Instance.AddConfig(name);
                                if (Volken.Instance.planetConfigList.ExistsInConfig(
                                    Game.Instance.FlightScene.CraftNode.Parent.Name))
                                {
                                    Volken.Instance.planetConfigList.SetConfig(
                                        Game.Instance.FlightScene.CraftNode.Parent.Name, name);
                                }
                                else
                                {
                                    Volken.Instance.planetConfigList.AddConfig(
                                        Game.Instance.FlightScene.CraftNode.Parent.Name, name);
                                }
                                Volken.Instance.RefreshConfigList();
                                inspectorPanel.Visible = false;
                                RebuildInspectorPanel();
                                Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                                    string.Format(Locale.GetString("Volken.UI.ConfigSavedAs"),
                                        Game.Instance.FlightScene.CraftNode.Parent.Name, name));
                            }
                        }
                        catch (Exception ex)
                        {
                            Mod.LOG("Volken: Error saving new config: " + ex);
                            Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                                Locale.GetString("Volken.UI.ErrorSavingNewConfig"));
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
        configManagementGroup.Add(saveAsButton);

        var loadConfigDropdown = new DropdownModel(
            Locale.GetString("Volken.UI.LoadConfig"),
            () => mainLayer.currentConfigName,
            (newConfig) =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(newConfig) && newConfig != mainLayer.currentConfigName)
                    {
                        var loadedConfig = CloudConfig.LoadFromFile(
                            Game.Instance.FlightScene.CraftNode.Parent.Name, newConfig);
                        mainLayer.config.CopyFrom(loadedConfig);
                        mainLayer.currentConfigName = newConfig;
                        Volken.Instance.ValueChanged();

                        if (Volken.Instance.planetConfigList.ExistsInConfig(
                            Game.Instance.FlightScene.CraftNode.Parent.Name))
                        {
                            Volken.Instance.planetConfigList.SetConfig(
                                Game.Instance.FlightScene.CraftNode.Parent.Name, mainLayer.currentConfigName);
                        }
                        else
                        {
                            Volken.Instance.planetConfigList.AddConfig(
                                Game.Instance.FlightScene.CraftNode.Parent.Name, mainLayer.currentConfigName);
                        }
                        Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                            string.Format(Locale.GetString("Volken.UI.ConfigLoaded"), newConfig));
                    }
                }
                catch (Exception ex)
                {
                    Mod.LOG("Volken: Error loading config: " + ex);
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                        Locale.GetString("Volken.UI.ErrorLoadingConfig"));
                }
            },
            Volken.Instance._availableConfigs);
        configManagementGroup.Add(loadConfigDropdown);

        var resetToDefaultButton = new TextButtonModel(Locale.GetString("Volken.UI.ResetCurrentToDefault"),
            (Action<TextButtonModel>)(b =>
            {
                try
                {
                    mainLayer.config.CopyFrom(CloudConfig.CreateDefault());
                    Volken.Instance.ValueChanged();
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                        Locale.GetString("Volken.UI.ConfigResetToDefaults"));
                }
                catch (Exception ex)
                {
                    Mod.LOG("Volken: Error resetting config: " + ex);
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                        Locale.GetString("Volken.UI.ErrorResettingConfig"));
                }
            }));
        configManagementGroup.Add(resetToDefaultButton);

        var tryAnotherButton = new TextButtonModel(Locale.GetString("Volken.UI.TryAnotherConfig"),
            (Action<TextButtonModel>)(b =>
            {
                try
                {
                    mainLayer.config.CopyFrom(CloudConfig.CreateAnotherDefault());
                    Volken.Instance.ValueChanged();
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                        Locale.GetString("Volken.UI.ConfigSetToDefaultII"));
                }
                catch (Exception ex)
                {
                    Mod.LOG("Volken: Error setting config: " + ex);
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                        Locale.GetString("Volken.UI.ErrorGettingConfig"));
                }
            }));
        configManagementGroup.Add(tryAnotherButton);

        inspectorModel.Add(configManagementGroup);
    }

    private void CreateExtraConfigManagementGroup(CloudLayer layer, string title)
    {
        GroupModel group = new GroupModel(string.Format(Locale.GetString("Volken.UI.ExtraLayerConfig"), title));
        string planet = Game.Instance.FlightScene.CraftNode.Parent.Name;

        var currentLabel = new TextModel(Locale.GetString("Volken.UI.CurrentConfig"),
            () => layer.currentConfigName ?? "Default");
        group.Add(currentLabel);

        var saveCurrentButton = new TextButtonModel(
            string.Format(Locale.GetString("Volken.UI.ExtraLayerSaveCurrent"), title),
            (Action<TextButtonModel>)(b =>
            {
                try
                {
                    string name = layer.currentConfigName ?? "Default";
                    layer.config.SaveToFile(planet, name);
                    Volken.Instance.RefreshConfigList();
                    if (!Volken.Instance.planetConfigList.ExistsInConfig(planet))
                        Volken.Instance.planetConfigList.AddConfig(planet, "Default", name);
                    else
                        Volken.Instance.planetConfigList.SetConfig(planet, name, 1);
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                        string.Format(Locale.GetString("Volken.UI.ExtraLayerConfigSaved"), title));
                }
                catch (Exception ex) { Mod.LOG("Volken: Error saving config: " + ex); }
            }));
        group.Add(saveCurrentButton);

        var saveAsButton = new TextButtonModel(
            string.Format(Locale.GetString("Volken.UI.ExtraLayerSaveAs"), title),
            (Action<TextButtonModel>)(b =>
            {
                try
                {
                    var dialog = Game.Instance.UserInterface.CreateInputDialog();
                    dialog.MessageText = string.Format(Locale.GetString("Volken.UI.ExtraLayerEnterPresetName"), title);
                    dialog.InputText = layer.currentConfigName ?? "Default";
                    dialog.OkayClicked += (inputDialog) =>
                    {
                        try
                        {
                            string name = inputDialog.InputText;
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                layer.config.SaveToFile(planet, name);
                                layer.currentConfigName = name;
                                Volken.Instance.RefreshConfigList();
                                if (!Volken.Instance.planetConfigList.ExistsInConfig(planet))
                                    Volken.Instance.planetConfigList.AddConfig(planet, "Default", name);
                                else
                                    Volken.Instance.planetConfigList.SetConfig(planet, name, 1);
                                Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                                    string.Format(Locale.GetString("Volken.UI.ExtraLayerConfigSavedAs"), title, name));
                                RebuildInspectorPanel();
                            }
                        }
                        catch (Exception ex) { Mod.LOG("Volken: Error saving: " + ex); }
                        finally { inputDialog?.Close(); }
                    };
                }
                catch (Exception ex) { Mod.LOG("Volken: Error creating dialog: " + ex); }
            }));
        group.Add(saveAsButton);

        var loadDropdown = new DropdownModel(
            string.Format(Locale.GetString("Volken.UI.ExtraLayerLoadPreset"), title),
            () => layer.currentConfigName ?? "Default",
            (newConfig) =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(newConfig))
                    {
                        var loaded = CloudConfig.LoadFromFile(planet, newConfig);
                        layer.config.CopyFrom(loaded);
                        layer.currentConfigName = newConfig;
                        Volken.Instance.ValueChanged();
                        if (!Volken.Instance.planetConfigList.ExistsInConfig(planet))
                            Volken.Instance.planetConfigList.AddConfig(planet, "Default", newConfig);
                        else
                            Volken.Instance.planetConfigList.SetConfig(planet, newConfig, 1);
                        Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                            string.Format(Locale.GetString("Volken.UI.ExtraLayerConfigLoaded"), title, newConfig));
                    }
                }
                catch (Exception ex) { Mod.LOG("Volken: Error loading: " + ex); }
            },
            Volken.Instance._availableConfigs);
        group.Add(loadDropdown);

        inspectorModel.Add(group);
    }

    #endregion

    #region Layer Groups

    /// <summary>
    /// Creates full UI group for the main cloud layer.
    /// </summary>
    private void CreateLayerGroup(CloudLayer layer, string title)
    {
        GroupModel group = new GroupModel(Locale.GetString("Volken.UI.Clouds") + " [" + title + "]");
        var cfg = layer.config;

        // Enable Toggle
        var renderToggleModel = new ToggleModel(Locale.GetString("Volken.UI.MainToggle"),
            () => cfg.enabled, s =>
            {
                if (!Game.Instance.FlightScene.CraftNode.Parent.PlanetData.AtmosphereData.HasPhysicsAtmosphere)
                {
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.NoCloudsHere"));
                    cfg.enabled = false;
                    return;
                }
                if (Game.Instance.FlightScene.CraftNode.Parent.Parent == null)
                {
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.NoStarClouds"));
                    cfg.enabled = false;
                    return;
                }
                cfg.enabled = s;
                Volken.Instance.ValueChanged();
            });
        group.Add(renderToggleModel);

        // Composite Mode
        var compositeDropdown = new DropdownModel("Composite Mode",
            () => cfg.compositeMode == CompositeMode.Additive ? "Additive" : "Standard",
            (val) =>
            {
                cfg.compositeMode = val == "Standard" ? CompositeMode.Standard : CompositeMode.Additive;
                Volken.Instance.ValueChanged();
            },
            new System.Collections.Generic.List<string> { "Additive", "Standard" });
        group.Add(compositeDropdown);

        // === Cloud Shape ===
        CreateSlider(group, Locale.GetString("Volken.UI.Density"), () => cfg.density,
            s => { cfg.density = s; Volken.Instance.ValueChanged(); }, 0.0001f, 0.05f, 4);
        CreateSlider(group, Locale.GetString("Volken.UI.Absorption"), () => cfg.absorption,
            s => { cfg.absorption = s; Volken.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.AmbientLight"), () => cfg.ambientLight,
            s => { cfg.ambientLight = s; Volken.Instance.ValueChanged(); }, 0.0f, 0.5f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.Coverage"), () => cfg.coverage,
            s => { cfg.coverage = s; Volken.Instance.ValueChanged(); }, -2.0f, 2.0f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.ShapeScale"), () => cfg.shapeScale,
            s => { cfg.shapeScale = s; Volken.Instance.ValueChanged(); }, 1000.0f, 50000.0f, 0);
        CreateSlider(group, Locale.GetString("Volken.UI.DetailScale"), () => cfg.detailScale,
            s => { cfg.detailScale = s; Volken.Instance.ValueChanged(); }, 500.0f, 25000.0f, 0);
        CreateSlider(group, Locale.GetString("Volken.UI.DetailStrength"), () => cfg.detailStrength,
            s => { cfg.detailStrength = s; Volken.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.CloudMovementSpeed"), () => cfg.windSpeed,
            s => { cfg.windSpeed = s; Volken.Instance.ValueChanged(); }, -0.05f, 0.05f, 4);
        CreateSlider(group, Locale.GetString("Volken.UI.WindDirection"), () => cfg.windDirection,
            s => { cfg.windDirection = s; Volken.Instance.ValueChanged(); }, 0.0f, 360.0f, 0, true);

        CreateSlider(group, Locale.GetString("Volken.UI.GlobalRotationAngular"), () => cfg.globalRotationAngular,
            s => { cfg.globalRotationAngular = s; Volken.Instance.ValueChanged(); }, -2.0f, 2.0f, 2);

        // Cloud Color
        CreateSlider(group, Locale.GetString("Volken.UI.CloudColorRed"), () => cfg.cloudColor.r,
            s => { var c = cfg.cloudColor; c.r = s; cfg.cloudColor = c; Volken.Instance.ValueChanged(); },
            0.0f, 1.0f, 0, false, true);
        CreateSlider(group, Locale.GetString("Volken.UI.CloudColorGreen"), () => cfg.cloudColor.g,
            s => { var c = cfg.cloudColor; c.g = s; cfg.cloudColor = c; Volken.Instance.ValueChanged(); },
            0.0f, 1.0f, 0, false, true);
        CreateSlider(group, Locale.GetString("Volken.UI.CloudColorBlue"), () => cfg.cloudColor.b,
            s => { var c = cfg.cloudColor; c.b = s; cfg.cloudColor = c; Volken.Instance.ValueChanged(); },
            0.0f, 1.0f, 0, false, true);

        // Scattering
        CreateSlider(group, Locale.GetString("Volken.UI.ScatterStrength"), () => cfg.scatterStrength,
            s => { cfg.scatterStrength = s; Volken.Instance.ValueChanged(); }, 0.0f, 2.0f, 3);
        CreateSlider(group, Locale.GetString("Volken.UI.AtmosphereBlendFactor"), () => cfg.atmoBlendFactor,
            s => { cfg.atmoBlendFactor = s; Volken.Instance.ValueChanged(); }, 0.0f, 50.0f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.ScatterPower"), () => cfg.scatterPower,
            s => { cfg.scatterPower = s; Volken.Instance.ValueChanged(); }, 1.0f, 2.5f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.MultiScatterBlend"), () => cfg.multiScatterBlend,
            s => { cfg.multiScatterBlend = s; Volken.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.AmbientScatter"), () => cfg.ambientScatterStrength,
            s => { cfg.ambientScatterStrength = s; Volken.Instance.ValueChanged(); }, 0.0f, 2.0f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.SilverLiningIntensity"), () => cfg.silverLiningIntensity,
            s => { cfg.silverLiningIntensity = s; Volken.Instance.ValueChanged(); }, 0.0f, 3.0f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.ForwardScatterBias"), () => cfg.forwardScatteringBias,
            s => { cfg.forwardScatteringBias = s; Volken.Instance.ValueChanged(); }, 0.0f, 0.99f, 2);

        // === Container Settings ===
        GroupModel containerGroup = new GroupModel(Locale.GetString("Volken.UI.CloudContainer") + " [" + title + "]");
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer1Height"), () => cfg.layerHeights.x,
            s => { var v = cfg.layerHeights; v.x = s; cfg.layerHeights = v; Volken.Instance.ValueChanged(); },
            500.0f, 10000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer1Spread"), () => cfg.layerSpreads.x,
            s => { var v = cfg.layerSpreads; v.x = s; cfg.layerSpreads = v; Volken.Instance.ValueChanged(); },
            100.0f, 5000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer1Strength"), () => cfg.layerStrengths.x,
            s => { var v = cfg.layerStrengths; v.x = s; cfg.layerStrengths = v; Volken.Instance.ValueChanged(); },
            0.0f, 2.0f, 1);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer2Height"), () => cfg.layerHeights.y,
            s => { var v = cfg.layerHeights; v.y = s; cfg.layerHeights = v; Volken.Instance.ValueChanged(); },
            500.0f, 10000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer2Spread"), () => cfg.layerSpreads.y,
            s => { var v = cfg.layerSpreads; v.y = s; cfg.layerSpreads = v; Volken.Instance.ValueChanged(); },
            100.0f, 5000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer2Strength"), () => cfg.layerStrengths.y,
            s => { var v = cfg.layerStrengths; v.y = s; cfg.layerStrengths = v; Volken.Instance.ValueChanged(); },
            0.0f, 2.0f, 1);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer3Height"), () => cfg.layerHeights.z,
            s => { var v = cfg.layerHeights; v.z = s; cfg.layerHeights = v; Volken.Instance.ValueChanged(); },
            500.0f, 20000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer3Spread"), () => cfg.layerSpreads.z,
            s => { var v = cfg.layerSpreads; v.z = s; cfg.layerSpreads = v; Volken.Instance.ValueChanged(); },
            100.0f, 10000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer3Strength"), () => cfg.layerStrengths.z,
            s => { var v = cfg.layerStrengths; v.z = s; cfg.layerStrengths = v; Volken.Instance.ValueChanged(); },
            0.0f, 2.0f, 1);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer4Height"), () => cfg.layerHeights.w,
            s => { var v = cfg.layerHeights; v.w = s; cfg.layerHeights = v; Volken.Instance.ValueChanged(); },
            500.0f, 20000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer4Spread"), () => cfg.layerSpreads.w,
            s => { var v = cfg.layerSpreads; v.w = s; cfg.layerSpreads = v; Volken.Instance.ValueChanged(); },
            100.0f, 10000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer4Strength"), () => cfg.layerStrengths.w,
            s => { var v = cfg.layerStrengths; v.w = s; cfg.layerStrengths = v; Volken.Instance.ValueChanged(); },
            0.0f, 2.0f, 1);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.MaxCloudHeight"), () => cfg.maxCloudHeight,
            s => { cfg.maxCloudHeight = s; Volken.Instance.ValueChanged(); }, 1000.0f, 25000.0f, 0);
        group.Add(containerGroup);

        // === Quality ===
        GroupModel qualityGroup = new GroupModel(Locale.GetString("Volken.UI.CloudQuality") + " [" + title + "]");
        CreateSlider(qualityGroup, Locale.GetString("Volken.UI.ResolutionScale"), () => cfg.resolutionScale,
            s => { cfg.resolutionScale = Mathf.Clamp(s, 0.1f, 1.0f); }, 0.1f, 1.0f, 2);
        CreateSlider(qualityGroup, Locale.GetString("Volken.UI.StepSize"), () => cfg.stepSize,
            s => { cfg.stepSize = s; Volken.Instance.ValueChanged(); }, 100.0f, 2000.0f, 0);
        CreateSlider(qualityGroup, Locale.GetString("Volken.UI.StepSizeFalloff"), () => cfg.stepSizeFalloff,
            s => { cfg.stepSizeFalloff = s; Volken.Instance.ValueChanged(); }, 0.1f, 3.0f, 2);
        CreateSlider(qualityGroup, Locale.GetString("Volken.UI.NumberOfLightSamples"), () => cfg.numLightSamplePoints,
            s => { cfg.numLightSamplePoints = Mathf.RoundToInt(s); Volken.Instance.ValueChanged(); }, 1, 25, 0, true);
        CreateSlider(qualityGroup, Locale.GetString("Volken.UI.RayOffsetStrength"), () => cfg.blueNoiseStrength,
            s => { cfg.blueNoiseStrength = s; Volken.Instance.ValueChanged(); }, 0.0f, 10.0f, 1);
        CreateSlider(qualityGroup, Locale.GetString("Volken.UI.HistoryBlend"), () => cfg.historyBlend,
            s => { cfg.historyBlend = s; Volken.Instance.ValueChanged(); }, 0.0f, 0.99f, 2);
        group.Add(qualityGroup);

        inspectorModel.Add(group);
    }

    /// <summary>
    /// Creates a full UI group for extra cloud layers (same controls as Main layer).
    /// </summary>
    private void CreateExtraLayerGroup(CloudLayer layer, string title)
    {
        GroupModel group = new GroupModel(string.Format(Locale.GetString("Volken.UI.ExtraLayer"), title));
        var cfg = layer.config;

        // Enable Toggle
        var renderToggleModel = new ToggleModel(
            string.Format(Locale.GetString("Volken.UI.ExtraLayerEnabled"), title),
            () => cfg.enabled, s =>
            {
                if (!Game.Instance.FlightScene.CraftNode.Parent.PlanetData.AtmosphereData.HasPhysicsAtmosphere)
                {
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.NoCloudsHere"));
                    cfg.enabled = false;
                    return;
                }
                cfg.enabled = s;
                Volken.Instance.ValueChanged();
            });
        group.Add(renderToggleModel);

        // Composite Mode
        var compositeDropdown = new DropdownModel(
            Locale.GetString("Volken.UI.CompositeMode"),
            () => cfg.compositeMode == CompositeMode.Additive ? "Additive" : "Standard",
            (val) =>
            {
                cfg.compositeMode = val == "Standard" ? CompositeMode.Standard : CompositeMode.Additive;
                Volken.Instance.ValueChanged();
            },
            new System.Collections.Generic.List<string> { "Additive", "Standard" });
        group.Add(compositeDropdown);

        // === Cloud Shape ===
        CreateSlider(group, Locale.GetString("Volken.UI.Density"), () => cfg.density,
            s => { cfg.density = s; Volken.Instance.ValueChanged(); }, 0.0001f, 0.05f, 4);
        CreateSlider(group, Locale.GetString("Volken.UI.Absorption"), () => cfg.absorption,
            s => { cfg.absorption = s; Volken.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.AmbientLight"), () => cfg.ambientLight,
            s => { cfg.ambientLight = s; Volken.Instance.ValueChanged(); }, 0.0f, 0.5f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.Coverage"), () => cfg.coverage,
            s => { cfg.coverage = s; Volken.Instance.ValueChanged(); }, -2.0f, 2.0f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.ShapeScale"), () => cfg.shapeScale,
            s => { cfg.shapeScale = s; Volken.Instance.ValueChanged(); }, 1000.0f, 50000.0f, 0);
        CreateSlider(group, Locale.GetString("Volken.UI.DetailScale"), () => cfg.detailScale,
            s => { cfg.detailScale = s; Volken.Instance.ValueChanged(); }, 500.0f, 25000.0f, 0);
        CreateSlider(group, Locale.GetString("Volken.UI.DetailStrength"), () => cfg.detailStrength,
            s => { cfg.detailStrength = s; Volken.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.CloudMovementSpeed"), () => cfg.windSpeed,
            s => { cfg.windSpeed = s; Volken.Instance.ValueChanged(); }, -0.05f, 0.05f, 4);
        CreateSlider(group, Locale.GetString("Volken.UI.WindDirection"), () => cfg.windDirection,
            s => { cfg.windDirection = s; Volken.Instance.ValueChanged(); }, 0.0f, 360.0f, 0, true);
        CreateSlider(group, Locale.GetString("Volken.UI.GlobalRotationAngular"), () => cfg.globalRotationAngular,
            s => { cfg.globalRotationAngular = s; Volken.Instance.ValueChanged(); }, -2.0f, 2.0f, 2);

        // Cloud Color
        CreateSlider(group, Locale.GetString("Volken.UI.CloudColorRed"), () => cfg.cloudColor.r,
            s => { var c = cfg.cloudColor; c.r = s; cfg.cloudColor = c; Volken.Instance.ValueChanged(); },
            0.0f, 1.0f, 0, false, true);
        CreateSlider(group, Locale.GetString("Volken.UI.CloudColorGreen"), () => cfg.cloudColor.g,
            s => { var c = cfg.cloudColor; c.g = s; cfg.cloudColor = c; Volken.Instance.ValueChanged(); },
            0.0f, 1.0f, 0, false, true);
        CreateSlider(group, Locale.GetString("Volken.UI.CloudColorBlue"), () => cfg.cloudColor.b,
            s => { var c = cfg.cloudColor; c.b = s; cfg.cloudColor = c; Volken.Instance.ValueChanged(); },
            0.0f, 1.0f, 0, false, true);

        // Scattering
        CreateSlider(group, Locale.GetString("Volken.UI.ScatterStrength"), () => cfg.scatterStrength,
            s => { cfg.scatterStrength = s; Volken.Instance.ValueChanged(); }, 0.0f, 2.0f, 3);
        CreateSlider(group, Locale.GetString("Volken.UI.AtmosphereBlendFactor"), () => cfg.atmoBlendFactor,
            s => { cfg.atmoBlendFactor = s; Volken.Instance.ValueChanged(); }, 0.0f, 50.0f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.ScatterPower"), () => cfg.scatterPower,
            s => { cfg.scatterPower = s; Volken.Instance.ValueChanged(); }, 1.0f, 2.5f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.MultiScatterBlend"), () => cfg.multiScatterBlend,
            s => { cfg.multiScatterBlend = s; Volken.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.AmbientScatter"), () => cfg.ambientScatterStrength,
            s => { cfg.ambientScatterStrength = s; Volken.Instance.ValueChanged(); }, 0.0f, 2.0f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.SilverLiningIntensity"), () => cfg.silverLiningIntensity,
            s => { cfg.silverLiningIntensity = s; Volken.Instance.ValueChanged(); }, 0.0f, 3.0f, 2);
        CreateSlider(group, Locale.GetString("Volken.UI.ForwardScatterBias"), () => cfg.forwardScatteringBias,
            s => { cfg.forwardScatteringBias = s; Volken.Instance.ValueChanged(); }, 0.0f, 0.99f, 2);

        // === Container Settings ===
        GroupModel containerGroup = new GroupModel(
            Locale.GetString("Volken.UI.CloudContainer") + " [" + title + "]");
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer1Height"), () => cfg.layerHeights.x,
            s => { var v = cfg.layerHeights; v.x = s; cfg.layerHeights = v; Volken.Instance.ValueChanged(); },
            500.0f, 30000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer1Spread"), () => cfg.layerSpreads.x,
            s => { var v = cfg.layerSpreads; v.x = s; cfg.layerSpreads = v; Volken.Instance.ValueChanged(); },
            100.0f, 10000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer1Strength"), () => cfg.layerStrengths.x,
            s => { var v = cfg.layerStrengths; v.x = s; cfg.layerStrengths = v; Volken.Instance.ValueChanged(); },
            0.0f, 2.0f, 1);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer2Height"), () => cfg.layerHeights.y,
            s => { var v = cfg.layerHeights; v.y = s; cfg.layerHeights = v; Volken.Instance.ValueChanged(); },
            500.0f, 30000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer2Spread"), () => cfg.layerSpreads.y,
            s => { var v = cfg.layerSpreads; v.y = s; cfg.layerSpreads = v; Volken.Instance.ValueChanged(); },
            100.0f, 10000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer2Strength"), () => cfg.layerStrengths.y,
            s => { var v = cfg.layerStrengths; v.y = s; cfg.layerStrengths = v; Volken.Instance.ValueChanged(); },
            0.0f, 2.0f, 1);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer3Height"), () => cfg.layerHeights.z,
            s => { var v = cfg.layerHeights; v.z = s; cfg.layerHeights = v; Volken.Instance.ValueChanged(); },
            500.0f, 30000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer3Spread"), () => cfg.layerSpreads.z,
            s => { var v = cfg.layerSpreads; v.z = s; cfg.layerSpreads = v; Volken.Instance.ValueChanged(); },
            100.0f, 10000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer3Strength"), () => cfg.layerStrengths.z,
            s => { var v = cfg.layerStrengths; v.z = s; cfg.layerStrengths = v; Volken.Instance.ValueChanged(); },
            0.0f, 2.0f, 1);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer4Height"), () => cfg.layerHeights.w,
            s => { var v = cfg.layerHeights; v.w = s; cfg.layerHeights = v; Volken.Instance.ValueChanged(); },
            500.0f, 30000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer4Spread"), () => cfg.layerSpreads.w,
            s => { var v = cfg.layerSpreads; v.w = s; cfg.layerSpreads = v; Volken.Instance.ValueChanged(); },
            100.0f, 10000.0f, 0);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer4Strength"), () => cfg.layerStrengths.w,
            s => { var v = cfg.layerStrengths; v.w = s; cfg.layerStrengths = v; Volken.Instance.ValueChanged(); },
            0.0f, 2.0f, 1);
        CreateSlider(containerGroup, Locale.GetString("Volken.UI.MaxCloudHeight"), () => cfg.maxCloudHeight,
            s => { cfg.maxCloudHeight = s; Volken.Instance.ValueChanged(); }, 1000.0f, 50000.0f, 0);
        group.Add(containerGroup);

        // === Quality ===
        GroupModel qualityGroup = new GroupModel(
            Locale.GetString("Volken.UI.CloudQuality") + " [" + title + "]");
        CreateSlider(qualityGroup, Locale.GetString("Volken.UI.ResolutionScale"), () => cfg.resolutionScale,
            s => { cfg.resolutionScale = Mathf.Clamp(s, 0.1f, 1.0f); }, 0.1f, 1.0f, 2);
        CreateSlider(qualityGroup, Locale.GetString("Volken.UI.StepSize"), () => cfg.stepSize,
            s => { cfg.stepSize = s; Volken.Instance.ValueChanged(); }, 100.0f, 3000.0f, 0);
        CreateSlider(qualityGroup, Locale.GetString("Volken.UI.StepSizeFalloff"), () => cfg.stepSizeFalloff,
            s => { cfg.stepSizeFalloff = s; Volken.Instance.ValueChanged(); }, 0.1f, 3.0f, 2);
        CreateSlider(qualityGroup, Locale.GetString("Volken.UI.NumberOfLightSamples"), () => cfg.numLightSamplePoints,
            s => { cfg.numLightSamplePoints = Mathf.RoundToInt(s); Volken.Instance.ValueChanged(); }, 1, 25, 0, true);
        CreateSlider(qualityGroup, Locale.GetString("Volken.UI.RayOffsetStrength"), () => cfg.blueNoiseStrength,
            s => { cfg.blueNoiseStrength = s; Volken.Instance.ValueChanged(); }, 0.0f, 10.0f, 1);
        CreateSlider(qualityGroup, Locale.GetString("Volken.UI.HistoryBlend"), () => cfg.historyBlend,
            s => { cfg.historyBlend = s; Volken.Instance.ValueChanged(); }, 0.0f, 0.99f, 2);
        group.Add(qualityGroup);

        inspectorModel.Add(group);
    }

    private static SliderModel CreateSlider(GroupModel group, string label,
        Func<float> getter, Action<float> setter,
        float min, float max, int decimals, bool isInteger = false, bool isColor = false)
    {
        var model = new SliderModel(label, getter, s => setter(s), min, max, isInteger);
        model.ValueFormatter = isColor
            ? (f => ((f / 1) * 255).ToString("N0"))
            : (f => f.ToString("n" + Mathf.Max(0, decimals)));
        group.Add(model);
        return model;
    }

    #endregion

    public void RebuildInspectorPanel()
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
                catch { /* ignore */ }
                inspectorPanel = null;
            }

            CreateInspectorPanel();
        }
        catch (Exception ex)
        {
            Mod.LOG("Volken: Error rebuilding panel: " + ex);
        }
    }

    private void OnDestroy()
    {
        if (inspectorPanel != null)
        {
            try
            {
                inspectorPanel.CloseButtonClicked -= OnCloseButtonClicked;
            }
            catch { }
        }
    }
}
