using System;
using System.Linq;
using System.Xml.Linq;
using Assets.Scripts;
using Assets.Scripts.Cameras;
using Assets.Scripts.Flight.MapView;
using Assets.Scripts.Terrain.Rendering;
using Assets.Scripts.Ui;
using ModApi;
using ModApi.Craft;
using ModApi.Flight.Sim;
using ModApi.Scenes.Events;
using ModApi.Ui;
using ModApi.Ui.Inspector;
using UnityEngine;

namespace Volken.Core
{
    using Volken.Clouds;
    using Volken.Weather;

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
            //  订阅放 Awake 而非 Start:必须早于 VolkenClouds(它也在 OnModLoaded 里订阅 SceneLoaded),
            // 因为别家 mod 的处理器抛异常会中断整条链,排在后面的订阅者会被静默跳掉
            try { Game.Instance.SceneManager.SceneLoaded += OnSceneLoaded; }
            catch (Exception ex) { Mod.Log("Volken: SceneLoaded subscribe failed: " + ex.Message); }
        }

        // 额外摄像机(PIP 等)体积云自动挂载
        // 不引用任何具体 mod 的类型:按 Unity 通用规则识别"渲染世界的额外相机"。
        // 开关 ModSettings.ExtraCameraClouds(默认开);远相机由 CloudRenderer 自行配对。
        private float _nextExtraCameraScanTime = -1f;

        private void Update()
        {
            try
            {
                if (!Game.InFlightScene) return;
                if (Time.realtimeSinceStartup < _nextExtraCameraScanTime) return;
                _nextExtraCameraScanTime = Time.realtimeSinceStartup + 1f;

                bool wantExtra = ModSettings.Instance == null || ModSettings.Instance.ExtraCameraClouds.Value;
                if (!wantExtra)
                {
                    return;
                }
                var gameCam = Game.Instance.FlightScene.ViewManager.GameView.GameCamera;
                // 只扫启用中的相机:FindObjectsOfType<Camera> 是每秒一次的全场景扫描,而禁用相机不渲染,也不需要云渲染器
                foreach (var cam in Camera.allCameras)
                {
                    if (cam == null) continue;
                    if (gameCam != null &&
                        (cam == gameCam.NearCamera || cam == gameCam.FarCamera)) continue;

                    var cr = cam.GetComponent<CloudRenderer>();
                    if (cr == null && IsExtraWorldCamera(cam))
                    {
                        cam.gameObject.AddComponent<CloudRenderer>();
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log("Volken: ExtraCameraClouds scan ERROR: " + ex.Message);
            }
        }

        private static bool IsExtraWorldCamera(Camera c)
        {
            if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) return false;
            if (c.targetTexture == null) return false;
            // JNO 专用相机脚本 → 排除(游戏 Near/Far、地图、UI、水面透明)
            if (c.GetComponent<SceneCameraScript>() != null) return false;
            if (c.GetComponent<MapCameraScript>() != null) return false;
            if (c.GetComponent<PrimaryUICameraScript>() != null) return false;
            if (c.GetComponent<WaterTransparencyCameraScript>() != null) return false;
            // 小 RT 排除(水面反射 256/512 方图等)
            if (c.targetTexture.width < 320 || c.targetTexture.height < 240) return false;
            try
            {
                var gameCam = Game.Instance.FlightScene.ViewManager.GameView.GameCamera;
                if (gameCam == null || gameCam.NearCamera == null) return false;
                int overlap = gameCam.NearCamera.cullingMask & c.cullingMask;
                if (overlap == 0) return false;
            }
            catch
            {
                return false;
            }
            return true;
        }

        private void Start()
        {
            Game.Instance.UserInterface.AddBuildUserInterfaceXmlAction(UserInterfaceIds.Flight.NavPanel, OnBuildFlightUI);
        }

        /// <summary>进飞行场景时建面板。**不做行星解析 / 云装配 / 大气门控** —— 那些属于 VolkenClouds。</summary>
        private void OnSceneLoaded(object sender, SceneEventArgs e)
        {
            if (e.Scene != "Flight") return;

            try
            {
                // 幂等兜底(正常路径已由 Mod.OnModLoaded 初始化过)
                Volken.Clouds.VolkenClouds.Initialize();

                CreateInspectorPanel();
                if (inspectorPanel != null)
                {
                    inspectorPanel.Visible = false;
                    inspectorPanel.CloseButtonClicked += OnCloseButtonClicked;
                }
            }
            catch (Exception ex)
            {
                Mod.Log("Volken: Error OnSceneLoaded: " + ex);
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
                Mod.Log("Volken: Error building flight UI: " + ex);
            }
        }

        public void OnToggleVolkenUI()
        {
            try
            {
                Volken.Clouds.VolkenClouds.Instance.RefreshConfigList();
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
                Mod.Log("Volken: Error toggling UI: " + ex);
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
                    Mod.Log("Volken: Error creating panel: " + createEx);
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
                        Mod.Log($"error in VolkenInterface.CreateInspectorPanel {e}");
                    }
                }

                inspectorModel = new InspectorModel("VolkenSettingsInspector",
                    "<color=green>" + Locale.GetString("Volken.UI.CloudSettings") + "</color>");

                var main = Volken.Clouds.VolkenClouds.Instance.MainLayer;
                if (main == null) return;

                // Config Management (uses MainLayer)
                CreateConfigManagementGroup(main);

                CreateLayerGroup(main, "Main");

                for (int i = 1; i < Volken.Clouds.VolkenClouds.Instance.layers.Count; i++)
                {
                    var layer = Volken.Clouds.VolkenClouds.Instance.layers[i];
                    if (layer != null)
                    {
                        CreateExtraConfigManagementGroup(layer, layer.displayName);
                        CreateExtraLayerGroup(layer, layer.displayName);
                    }
                }

                // —— 玩家只有一个"调这颗行星观感"的地方。
                WeatherPanel.Build(inspectorModel);

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
                Mod.Log("Volken: Error creating inspector panel: " + ex);
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
                        Mod.Log("Volken: Error saving config: " + ex);
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
                                    Volken.Clouds.VolkenClouds.Instance.AddConfig(name);
                                    if (Volken.Clouds.VolkenClouds.Instance.planetConfigList.ExistsInConfig(
                                        Game.Instance.FlightScene.CraftNode.Parent.Name))
                                    {
                                        Volken.Clouds.VolkenClouds.Instance.planetConfigList.SetConfig(
                                            Game.Instance.FlightScene.CraftNode.Parent.Name, name);
                                    }
                                    else
                                    {
                                        Volken.Clouds.VolkenClouds.Instance.planetConfigList.AddConfig(
                                            Game.Instance.FlightScene.CraftNode.Parent.Name, name);
                                    }
                                    Volken.Clouds.VolkenClouds.Instance.RefreshConfigList();
                                    inspectorPanel.Visible = false;
                                    RebuildInspectorPanel();
                                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                                        string.Format(Locale.GetString("Volken.UI.ConfigSavedAs"),
                                            Game.Instance.FlightScene.CraftNode.Parent.Name, name));
                                }
                            }
                            catch (Exception ex)
                            {
                                Mod.Log("Volken: Error saving new config: " + ex);
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
                        Mod.Log("Volken: Error creating save dialog: " + ex);
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
                            // 只换云层预设 —— 天气预设是**独立**的另一套名字,这里不碰
                            var loadedConfig = CloudConfig.LoadFromFile(
                                Game.Instance.FlightScene.CraftNode.Parent.Name, newConfig);
                            mainLayer.config.CopyFrom(loadedConfig);
                            mainLayer.currentConfigName = newConfig;
                            Volken.Clouds.VolkenClouds.Instance.ValueChanged();

                            if (Volken.Clouds.VolkenClouds.Instance.planetConfigList.ExistsInConfig(
                                Game.Instance.FlightScene.CraftNode.Parent.Name))
                            {
                                Volken.Clouds.VolkenClouds.Instance.planetConfigList.SetConfig(
                                    Game.Instance.FlightScene.CraftNode.Parent.Name, mainLayer.currentConfigName);
                            }
                            else
                            {
                                Volken.Clouds.VolkenClouds.Instance.planetConfigList.AddConfig(
                                    Game.Instance.FlightScene.CraftNode.Parent.Name, mainLayer.currentConfigName);
                            }
                            Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                                string.Format(Locale.GetString("Volken.UI.ConfigLoaded"), newConfig));
                        }
                    }
                    catch (Exception ex)
                    {
                        Mod.Log("Volken: Error loading config: " + ex);
                        Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                            Locale.GetString("Volken.UI.ErrorLoadingConfig"));
                    }
                },
                Volken.Clouds.VolkenClouds.Instance._availableConfigs);
            configManagementGroup.Add(loadConfigDropdown);

            var resetToDefaultButton = new TextButtonModel(Locale.GetString("Volken.UI.ResetCurrentToDefault"),
                (Action<TextButtonModel>)(b =>
                {
                    try
                    {
                        mainLayer.config.CopyFrom(CloudConfig.CreateDefault());
                        Volken.Clouds.VolkenClouds.Instance.ValueChanged();
                        Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                            Locale.GetString("Volken.UI.ConfigResetToDefaults"));
                    }
                    catch (Exception ex)
                    {
                        Mod.Log("Volken: Error resetting config: " + ex);
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
                        Volken.Clouds.VolkenClouds.Instance.ValueChanged();
                        Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                            Locale.GetString("Volken.UI.ConfigSetToDefaultII"));
                    }
                    catch (Exception ex)
                    {
                        Mod.Log("Volken: Error setting config: " + ex);
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
                        Volken.Clouds.VolkenClouds.Instance.RefreshConfigList();
                        if (!Volken.Clouds.VolkenClouds.Instance.planetConfigList.ExistsInConfig(planet))
                            Volken.Clouds.VolkenClouds.Instance.planetConfigList.AddConfig(planet, "Default", name);
                        else
                            Volken.Clouds.VolkenClouds.Instance.planetConfigList.SetConfig(planet, name, 1);
                        Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                            string.Format(Locale.GetString("Volken.UI.ExtraLayerConfigSaved"), title));
                    }
                    catch (Exception ex) { Mod.Log("Volken: Error saving config: " + ex); }
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
                                    Volken.Clouds.VolkenClouds.Instance.RefreshConfigList();
                                    if (!Volken.Clouds.VolkenClouds.Instance.planetConfigList.ExistsInConfig(planet))
                                        Volken.Clouds.VolkenClouds.Instance.planetConfigList.AddConfig(planet, "Default", name);
                                    else
                                        Volken.Clouds.VolkenClouds.Instance.planetConfigList.SetConfig(planet, name, 1);
                                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                                        string.Format(Locale.GetString("Volken.UI.ExtraLayerConfigSavedAs"), title, name));
                                    RebuildInspectorPanel();
                                }
                            }
                            catch (Exception ex) { Mod.Log("Volken: Error saving: " + ex); }
                            finally { inputDialog?.Close(); }
                        };
                    }
                    catch (Exception ex) { Mod.Log("Volken: Error creating dialog: " + ex); }
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
                            Volken.Clouds.VolkenClouds.Instance.ValueChanged();
                            if (!Volken.Clouds.VolkenClouds.Instance.planetConfigList.ExistsInConfig(planet))
                                Volken.Clouds.VolkenClouds.Instance.planetConfigList.AddConfig(planet, "Default", newConfig);
                            else
                                Volken.Clouds.VolkenClouds.Instance.planetConfigList.SetConfig(planet, newConfig, 1);
                            Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                                string.Format(Locale.GetString("Volken.UI.ExtraLayerConfigLoaded"), title, newConfig));
                        }
                    }
                    catch (Exception ex) { Mod.Log("Volken: Error loading: " + ex); }
                },
                Volken.Clouds.VolkenClouds.Instance._availableConfigs);
            group.Add(loadDropdown);

            inspectorModel.Add(group);
        }

        #endregion

        #region Layer Groups

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
                    Volken.Clouds.VolkenClouds.Instance.ValueChanged();
                });
            group.Add(renderToggleModel);

            // Composite Mode
            var compositeDropdown = new DropdownModel(Locale.GetString("Volken.UI.CompositeMode"),
                () => cfg.compositeMode == CompositeMode.Additive ? "Additive" : "Standard",
                (val) =>
                {
                    cfg.compositeMode = val == "Standard" ? CompositeMode.Standard : CompositeMode.Additive;
                    Volken.Clouds.VolkenClouds.Instance.ValueChanged();
                },
                new System.Collections.Generic.List<string> { "Additive", "Standard" });
            group.Add(compositeDropdown);

            // 方案 B: 游戏自带云作为全球分布形状(对比用)
            var stockToggleModel = new ToggleModel(Locale.GetString("Volken.UI.UseStockCloudMap"),
                () => cfg.useStockCloudMap, s =>
                {
                    if (s && StockCloudMap.Current == null)
                    {
                        Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.StockCloudUnavailable"));
                        cfg.useStockCloudMap = false;
                        return;
                    }
                    cfg.useStockCloudMap = s;
                    Volken.Clouds.VolkenClouds.Instance.ValueChanged();
                });
            group.Add(stockToggleModel);

            // 选择用游戏哪一层云作为分布(0=低,1=中,2=高,3=按层对应)
            var stockLayerOptions = new System.Collections.Generic.List<string>
            {
                Locale.GetString("Volken.UI.StockLayerLow"),
                Locale.GetString("Volken.UI.StockLayerMid"),
                Locale.GetString("Volken.UI.StockLayerHigh"),
                Locale.GetString("Volken.UI.StockLayerPerBand")
            };
            var stockLayerDropdown = new DropdownModel(Locale.GetString("Volken.UI.StockMapLayer"),
                () => stockLayerOptions[Mathf.Clamp(cfg.stockMapLayer, 0, 3)],
                (val) =>
                {
                    int idx = stockLayerOptions.IndexOf(val);
                    if (idx >= 0) cfg.stockMapLayer = idx;
                    Volken.Clouds.VolkenClouds.Instance.ValueChanged();
                },
                stockLayerOptions);
            group.Add(stockLayerDropdown);
            CreateSlider(group, Locale.GetString("Volken.UI.StockMapStrength"), () => cfg.stockMapStrength,
                s => { cfg.stockMapStrength = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.StockDensityScale"), () => cfg.stockDensityScale,
                s => { cfg.stockDensityScale = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.StockMaskInfluence"), () => cfg.stockMaskInfluence,
                s => { cfg.stockMaskInfluence = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.StockAlignSign"), () => cfg.stockAlignSign,
                s => { cfg.stockAlignSign = Mathf.Sign(s); Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, -1.0f, 1.0f, 0);
            CreateSlider(group, Locale.GetString("Volken.UI.StockAlignAngleOffset"), () => cfg.stockAlignAngleOffset,
                s => { cfg.stockAlignAngleOffset = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, -180.0f, 180.0f, 1);

            CreateSlider(group, Locale.GetString("Volken.UI.Density"), () => cfg.density,
                s => { cfg.density = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0001f, 0.05f, 4);
            CreateSlider(group, Locale.GetString("Volken.UI.Absorption"), () => cfg.absorption,
                s => { cfg.absorption = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.AmbientLight"), () => cfg.ambientLight,
                s => { cfg.ambientLight = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 0.5f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.Coverage"), () => cfg.coverage,
                s => { cfg.coverage = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, -2.0f, 2.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.ShapeScale"), () => cfg.shapeScale,
                s => { cfg.shapeScale = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 1000.0f, 50000.0f, 0);
            CreateSlider(group, Locale.GetString("Volken.UI.DetailScale"), () => cfg.detailScale,
                s => { cfg.detailScale = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 500.0f, 25000.0f, 0);
            CreateSlider(group, Locale.GetString("Volken.UI.DetailStrength"), () => cfg.detailStrength,
                s => { cfg.detailStrength = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.CloudMovementSpeed"), () => cfg.windSpeed,
                s => { cfg.windSpeed = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, -0.05f, 0.05f, 4);
            CreateSlider(group, Locale.GetString("Volken.UI.WindDirection"), () => cfg.windDirection,
                s => { cfg.windDirection = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 360.0f, 0, true);

            CreateSlider(group, Locale.GetString("Volken.UI.GlobalRotationAngular"), () => cfg.globalRotationAngular,
                s => { cfg.globalRotationAngular = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, -2.0f, 2.0f, 2);

            // Cloud Color
            CreateSlider(group, Locale.GetString("Volken.UI.CloudColorRed"), () => cfg.cloudColor.r,
                s => { var c = cfg.cloudColor; c.r = s; cfg.cloudColor = c; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                0.0f, 1.0f, 0, false, true);
            CreateSlider(group, Locale.GetString("Volken.UI.CloudColorGreen"), () => cfg.cloudColor.g,
                s => { var c = cfg.cloudColor; c.g = s; cfg.cloudColor = c; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                0.0f, 1.0f, 0, false, true);
            CreateSlider(group, Locale.GetString("Volken.UI.CloudColorBlue"), () => cfg.cloudColor.b,
                s => { var c = cfg.cloudColor; c.b = s; cfg.cloudColor = c; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                0.0f, 1.0f, 0, false, true);

            // Scattering
            CreateSlider(group, Locale.GetString("Volken.UI.ScatterStrength"), () => cfg.scatterStrength,
                s => { cfg.scatterStrength = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 2.0f, 3);
            CreateSlider(group, Locale.GetString("Volken.UI.AtmosphereBlendFactor"), () => cfg.atmoBlendFactor,
                s => { cfg.atmoBlendFactor = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 50.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.ScatterPower"), () => cfg.scatterPower,
                s => { cfg.scatterPower = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 1.0f, 2.5f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.MultiScatterBlend"), () => cfg.multiScatterBlend,
                s => { cfg.multiScatterBlend = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.AmbientScatter"), () => cfg.ambientScatterStrength,
                s => { cfg.ambientScatterStrength = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 2.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.SilverLiningIntensity"), () => cfg.silverLiningIntensity,
                s => { cfg.silverLiningIntensity = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 3.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.ForwardScatterBias"), () => cfg.forwardScatteringBias,
                s => { cfg.forwardScatteringBias = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 0.99f, 2);

            // Container Settings
            GroupModel containerGroup = new GroupModel(Locale.GetString("Volken.UI.CloudContainer") + " [" + title + "]");
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer1Height"), () => cfg.layerHeights.x,
                s => { var v = cfg.layerHeights; v.x = s; cfg.layerHeights = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                500.0f, 10000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer1Spread"), () => cfg.layerSpreads.x,
                s => { var v = cfg.layerSpreads; v.x = s; cfg.layerSpreads = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                100.0f, 5000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer1Strength"), () => cfg.layerStrengths.x,
                s => { var v = cfg.layerStrengths; v.x = s; cfg.layerStrengths = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                0.0f, 2.0f, 1);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer2Height"), () => cfg.layerHeights.y,
                s => { var v = cfg.layerHeights; v.y = s; cfg.layerHeights = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                500.0f, 10000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer2Spread"), () => cfg.layerSpreads.y,
                s => { var v = cfg.layerSpreads; v.y = s; cfg.layerSpreads = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                100.0f, 5000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer2Strength"), () => cfg.layerStrengths.y,
                s => { var v = cfg.layerStrengths; v.y = s; cfg.layerStrengths = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                0.0f, 2.0f, 1);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer3Height"), () => cfg.layerHeights.z,
                s => { var v = cfg.layerHeights; v.z = s; cfg.layerHeights = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                500.0f, 20000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer3Spread"), () => cfg.layerSpreads.z,
                s => { var v = cfg.layerSpreads; v.z = s; cfg.layerSpreads = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                100.0f, 10000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer3Strength"), () => cfg.layerStrengths.z,
                s => { var v = cfg.layerStrengths; v.z = s; cfg.layerStrengths = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                0.0f, 2.0f, 1);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer4Height"), () => cfg.layerHeights.w,
                s => { var v = cfg.layerHeights; v.w = s; cfg.layerHeights = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                500.0f, 20000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer4Spread"), () => cfg.layerSpreads.w,
                s => { var v = cfg.layerSpreads; v.w = s; cfg.layerSpreads = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                100.0f, 10000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer4Strength"), () => cfg.layerStrengths.w,
                s => { var v = cfg.layerStrengths; v.w = s; cfg.layerStrengths = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                0.0f, 2.0f, 1);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.MaxCloudHeight"), () => cfg.maxCloudHeight,
                s => { cfg.maxCloudHeight = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 1000.0f, 25000.0f, 0);
            group.Add(containerGroup);

            GroupModel qualityGroup = new GroupModel(Locale.GetString("Volken.UI.CloudQuality") + " [" + title + "]");
            // 方案 C: 时序超采样(A/B 对比用)
            var temporalToggleModel = new ToggleModel(Locale.GetString("Volken.UI.UseTemporalUpscale"),
                () => cfg.useTemporalUpscale, s =>
                {
                    cfg.useTemporalUpscale = s;
                    Volken.Clouds.VolkenClouds.Instance.ValueChanged();
                });
            qualityGroup.Add(temporalToggleModel);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.UpscaleGrid"), () => cfg.upscaleX,
                s => { int v = Mathf.Clamp(Mathf.RoundToInt(s), 1, 6); cfg.upscaleX = v; cfg.upscaleY = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 1, 6, 0, true);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.ResolutionScale"), () => cfg.resolutionScale,
                s => { cfg.resolutionScale = Mathf.Clamp(s, 0.1f, 1.0f); }, 0.1f, 1.0f, 2);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.StepSize"), () => cfg.stepSize,
                s => { cfg.stepSize = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 100.0f, 2000.0f, 0);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.StepSizeFalloff"), () => cfg.stepSizeFalloff,
                s => { cfg.stepSizeFalloff = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.1f, 3.0f, 2);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.NumberOfLightSamples"), () => cfg.numLightSamplePoints,
                s => { cfg.numLightSamplePoints = Mathf.RoundToInt(s); Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 1, 25, 0, true);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.LightMarchDistance"), () => cfg.lightMarchDistance,
                s => { cfg.lightMarchDistance = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 500.0f, 30000.0f, 0);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.RayOffsetStrength"), () => cfg.blueNoiseStrength,
                s => { cfg.blueNoiseStrength = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 10.0f, 1);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.HistoryBlend"), () => cfg.historyBlend,
                s => { cfg.historyBlend = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 0.99f, 2);
            group.Add(qualityGroup);

            // 轨道云(2D 壳着色 + 过渡带交叉淡入)
            GroupModel orbitGroup = new GroupModel(Locale.GetString("Volken.UI.OrbitClouds") + " [" + title + "]");
            CreateOrbitCloudsGroup(orbitGroup, cfg);
            group.Add(orbitGroup);

            inspectorModel.Add(group);
        }

        /// <summary>额外云层的控件组(与主层同款)。</summary>
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
                    Volken.Clouds.VolkenClouds.Instance.ValueChanged();
                });
            group.Add(renderToggleModel);

            // Composite Mode
            var compositeDropdown = new DropdownModel(
                Locale.GetString("Volken.UI.CompositeMode"),
                () => cfg.compositeMode == CompositeMode.Additive ? "Additive" : "Standard",
                (val) =>
                {
                    cfg.compositeMode = val == "Standard" ? CompositeMode.Standard : CompositeMode.Additive;
                    Volken.Clouds.VolkenClouds.Instance.ValueChanged();
                },
                new System.Collections.Generic.List<string> { "Additive", "Standard" });
            group.Add(compositeDropdown);

            // 方案 B/方案 A: 游戏自带云分布 + 区域内密度缩放(与主层一致)
            var stockToggleModel = new ToggleModel(Locale.GetString("Volken.UI.UseStockCloudMap"),
                () => cfg.useStockCloudMap, s =>
                {
                    if (s && StockCloudMap.Current == null)
                    {
                        Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.StockCloudUnavailable"));
                        cfg.useStockCloudMap = false;
                        return;
                    }
                    cfg.useStockCloudMap = s;
                    Volken.Clouds.VolkenClouds.Instance.ValueChanged();
                });
            group.Add(stockToggleModel);

            // 选择用游戏哪一层云作为分布(0=低,1=中,2=高,3=按层对应)
            var stockLayerOptions = new System.Collections.Generic.List<string>
            {
                Locale.GetString("Volken.UI.StockLayerLow"),
                Locale.GetString("Volken.UI.StockLayerMid"),
                Locale.GetString("Volken.UI.StockLayerHigh"),
                Locale.GetString("Volken.UI.StockLayerPerBand")
            };
            var stockLayerDropdown = new DropdownModel(Locale.GetString("Volken.UI.StockMapLayer"),
                () => stockLayerOptions[Mathf.Clamp(cfg.stockMapLayer, 0, 3)],
                (val) =>
                {
                    int idx = stockLayerOptions.IndexOf(val);
                    if (idx >= 0) cfg.stockMapLayer = idx;
                    Volken.Clouds.VolkenClouds.Instance.ValueChanged();
                },
                stockLayerOptions);
            group.Add(stockLayerDropdown);
            CreateSlider(group, Locale.GetString("Volken.UI.StockMapStrength"), () => cfg.stockMapStrength,
                s => { cfg.stockMapStrength = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.StockDensityScale"), () => cfg.stockDensityScale,
                s => { cfg.stockDensityScale = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.StockMaskInfluence"), () => cfg.stockMaskInfluence,
                s => { cfg.stockMaskInfluence = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.StockAlignSign"), () => cfg.stockAlignSign,
                s => { cfg.stockAlignSign = Mathf.Sign(s); Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, -1.0f, 1.0f, 0);
            CreateSlider(group, Locale.GetString("Volken.UI.StockAlignAngleOffset"), () => cfg.stockAlignAngleOffset,
                s => { cfg.stockAlignAngleOffset = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, -180.0f, 180.0f, 1);

            CreateSlider(group, Locale.GetString("Volken.UI.Density"), () => cfg.density,
                s => { cfg.density = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0001f, 0.05f, 4);
            CreateSlider(group, Locale.GetString("Volken.UI.Absorption"), () => cfg.absorption,
                s => { cfg.absorption = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.AmbientLight"), () => cfg.ambientLight,
                s => { cfg.ambientLight = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 0.5f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.Coverage"), () => cfg.coverage,
                s => { cfg.coverage = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, -2.0f, 2.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.ShapeScale"), () => cfg.shapeScale,
                s => { cfg.shapeScale = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 1000.0f, 50000.0f, 0);
            CreateSlider(group, Locale.GetString("Volken.UI.DetailScale"), () => cfg.detailScale,
                s => { cfg.detailScale = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 500.0f, 25000.0f, 0);
            CreateSlider(group, Locale.GetString("Volken.UI.DetailStrength"), () => cfg.detailStrength,
                s => { cfg.detailStrength = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.CloudMovementSpeed"), () => cfg.windSpeed,
                s => { cfg.windSpeed = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, -0.05f, 0.05f, 4);
            CreateSlider(group, Locale.GetString("Volken.UI.WindDirection"), () => cfg.windDirection,
                s => { cfg.windDirection = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 360.0f, 0, true);
            CreateSlider(group, Locale.GetString("Volken.UI.GlobalRotationAngular"), () => cfg.globalRotationAngular,
                s => { cfg.globalRotationAngular = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, -2.0f, 2.0f, 2);

            // Cloud Color
            CreateSlider(group, Locale.GetString("Volken.UI.CloudColorRed"), () => cfg.cloudColor.r,
                s => { var c = cfg.cloudColor; c.r = s; cfg.cloudColor = c; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                0.0f, 1.0f, 0, false, true);
            CreateSlider(group, Locale.GetString("Volken.UI.CloudColorGreen"), () => cfg.cloudColor.g,
                s => { var c = cfg.cloudColor; c.g = s; cfg.cloudColor = c; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                0.0f, 1.0f, 0, false, true);
            CreateSlider(group, Locale.GetString("Volken.UI.CloudColorBlue"), () => cfg.cloudColor.b,
                s => { var c = cfg.cloudColor; c.b = s; cfg.cloudColor = c; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                0.0f, 1.0f, 0, false, true);

            // Scattering
            CreateSlider(group, Locale.GetString("Volken.UI.ScatterStrength"), () => cfg.scatterStrength,
                s => { cfg.scatterStrength = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 2.0f, 3);
            CreateSlider(group, Locale.GetString("Volken.UI.AtmosphereBlendFactor"), () => cfg.atmoBlendFactor,
                s => { cfg.atmoBlendFactor = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 50.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.ScatterPower"), () => cfg.scatterPower,
                s => { cfg.scatterPower = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 1.0f, 2.5f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.MultiScatterBlend"), () => cfg.multiScatterBlend,
                s => { cfg.multiScatterBlend = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 1.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.AmbientScatter"), () => cfg.ambientScatterStrength,
                s => { cfg.ambientScatterStrength = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 2.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.SilverLiningIntensity"), () => cfg.silverLiningIntensity,
                s => { cfg.silverLiningIntensity = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 3.0f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.ForwardScatterBias"), () => cfg.forwardScatteringBias,
                s => { cfg.forwardScatteringBias = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 0.99f, 2);

            // Container Settings
            GroupModel containerGroup = new GroupModel(
                Locale.GetString("Volken.UI.CloudContainer") + " [" + title + "]");
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer1Height"), () => cfg.layerHeights.x,
                s => { var v = cfg.layerHeights; v.x = s; cfg.layerHeights = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                500.0f, 30000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer1Spread"), () => cfg.layerSpreads.x,
                s => { var v = cfg.layerSpreads; v.x = s; cfg.layerSpreads = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                100.0f, 10000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer1Strength"), () => cfg.layerStrengths.x,
                s => { var v = cfg.layerStrengths; v.x = s; cfg.layerStrengths = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                0.0f, 2.0f, 1);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer2Height"), () => cfg.layerHeights.y,
                s => { var v = cfg.layerHeights; v.y = s; cfg.layerHeights = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                500.0f, 30000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer2Spread"), () => cfg.layerSpreads.y,
                s => { var v = cfg.layerSpreads; v.y = s; cfg.layerSpreads = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                100.0f, 10000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer2Strength"), () => cfg.layerStrengths.y,
                s => { var v = cfg.layerStrengths; v.y = s; cfg.layerStrengths = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                0.0f, 2.0f, 1);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer3Height"), () => cfg.layerHeights.z,
                s => { var v = cfg.layerHeights; v.z = s; cfg.layerHeights = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                500.0f, 30000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer3Spread"), () => cfg.layerSpreads.z,
                s => { var v = cfg.layerSpreads; v.z = s; cfg.layerSpreads = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                100.0f, 10000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer3Strength"), () => cfg.layerStrengths.z,
                s => { var v = cfg.layerStrengths; v.z = s; cfg.layerStrengths = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                0.0f, 2.0f, 1);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer4Height"), () => cfg.layerHeights.w,
                s => { var v = cfg.layerHeights; v.w = s; cfg.layerHeights = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                500.0f, 30000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer4Spread"), () => cfg.layerSpreads.w,
                s => { var v = cfg.layerSpreads; v.w = s; cfg.layerSpreads = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                100.0f, 10000.0f, 0);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.Layer4Strength"), () => cfg.layerStrengths.w,
                s => { var v = cfg.layerStrengths; v.w = s; cfg.layerStrengths = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); },
                0.0f, 2.0f, 1);
            CreateSlider(containerGroup, Locale.GetString("Volken.UI.MaxCloudHeight"), () => cfg.maxCloudHeight,
                s => { cfg.maxCloudHeight = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 1000.0f, 50000.0f, 0);
            group.Add(containerGroup);

            GroupModel qualityGroup = new GroupModel(
                Locale.GetString("Volken.UI.CloudQuality") + " [" + title + "]");
            // 方案 C: 时序超采样(A/B 对比用)
            var temporalToggleModel = new ToggleModel(Locale.GetString("Volken.UI.UseTemporalUpscale"),
                () => cfg.useTemporalUpscale, s =>
                {
                    cfg.useTemporalUpscale = s;
                    Volken.Clouds.VolkenClouds.Instance.ValueChanged();
                });
            qualityGroup.Add(temporalToggleModel);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.UpscaleGrid"), () => cfg.upscaleX,
                s => { int v = Mathf.Clamp(Mathf.RoundToInt(s), 1, 6); cfg.upscaleX = v; cfg.upscaleY = v; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 1, 6, 0, true);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.ResolutionScale"), () => cfg.resolutionScale,
                s => { cfg.resolutionScale = Mathf.Clamp(s, 0.1f, 1.0f); }, 0.1f, 1.0f, 2);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.StepSize"), () => cfg.stepSize,
                s => { cfg.stepSize = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 100.0f, 3000.0f, 0);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.StepSizeFalloff"), () => cfg.stepSizeFalloff,
                s => { cfg.stepSizeFalloff = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.1f, 3.0f, 2);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.NumberOfLightSamples"), () => cfg.numLightSamplePoints,
                s => { cfg.numLightSamplePoints = Mathf.RoundToInt(s); Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 1, 25, 0, true);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.LightMarchDistance"), () => cfg.lightMarchDistance,
                s => { cfg.lightMarchDistance = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 500.0f, 30000.0f, 0);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.RayOffsetStrength"), () => cfg.blueNoiseStrength,
                s => { cfg.blueNoiseStrength = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 10.0f, 1);
            CreateSlider(qualityGroup, Locale.GetString("Volken.UI.HistoryBlend"), () => cfg.historyBlend,
                s => { cfg.historyBlend = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.0f, 0.99f, 2);
            group.Add(qualityGroup);

            // 轨道云(2D 壳着色 + 过渡带交叉淡入)
            GroupModel orbitGroup = new GroupModel(Locale.GetString("Volken.UI.OrbitClouds") + " [" + title + "]");
            CreateOrbitCloudsGroup(orbitGroup, cfg);
            group.Add(orbitGroup);

            inspectorModel.Add(group);
        }

        /// <summary>轨道云(2D 壳着色 + 过渡带交叉淡入)配置组。默认关闭 → 零回归。</summary>
        private static void CreateOrbitCloudsGroup(GroupModel group, CloudConfig cfg)
        {
            var orbitToggle = new ToggleModel(Locale.GetString("Volken.UI.UseOrbitClouds"),
                () => cfg.useOrbitClouds, s =>
                {
                    cfg.useOrbitClouds = s;
                    Volken.Clouds.VolkenClouds.Instance.ValueChanged();
                });
            group.Add(orbitToggle);

            CreateSlider(group, Locale.GetString("Volken.UI.OrbitTransitionStart"), () => cfg.orbitTransitionStartAltitude,
                s => { cfg.orbitTransitionStartAltitude = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0f, 200000f, 0);
            CreateSlider(group, Locale.GetString("Volken.UI.OrbitTransitionEnd"), () => cfg.orbitTransitionEndAltitude,
                s => { cfg.orbitTransitionEndAltitude = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0f, 500000f, 0);
            CreateSlider(group, Locale.GetString("Volken.UI.OrbitSampleAltitude"), () => cfg.orbitSampleAltitude,
                s => { cfg.orbitSampleAltitude = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0f, 50000f, 0);
            CreateSlider(group, Locale.GetString("Volken.UI.OrbitDensityBoost"), () => cfg.orbitDensityBoost,
                s => { cfg.orbitDensityBoost = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.1f, 5f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.OrbitBrightness"), () => cfg.orbitBrightness,
                s => { cfg.orbitBrightness = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0f, 2f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.OrbitReliefStrength"), () => cfg.orbitReliefStrength,
                s => { cfg.orbitReliefStrength = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0f, 4f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.OrbitDetailStrength"), () => cfg.orbitDetailStrength,
                s => { cfg.orbitDetailStrength = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0f, 1f, 2);
            CreateSlider(group, Locale.GetString("Volken.UI.OrbitResolutionScale"), () => cfg.orbitResolutionScale,
                s => { cfg.orbitResolutionScale = s; Volken.Clouds.VolkenClouds.Instance.ValueChanged(); }, 0.1f, 1f, 2);

            // 调试分屏开关:仅在 debug 模式(ModSettings.DevMode)下显示,平时对用户隐藏
            bool orbitDebugShown = false;
            try { orbitDebugShown = ModSettings.Instance != null && ModSettings.Instance.DevMode; } catch { }
            if (orbitDebugShown)
            {
                var debugToggle = new ToggleModel(Locale.GetString("Volken.UI.OrbitDebugMode"),
                    () => cfg.orbitDebugMode > 0.5f, s =>
                    {
                        cfg.orbitDebugMode = s ? 1f : 0f;
                        Volken.Clouds.VolkenClouds.Instance.ValueChanged();
                    });
                group.Add(debugToggle);
            }
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
                Mod.Log("Volken: Error rebuilding panel: " + ex);
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
}
