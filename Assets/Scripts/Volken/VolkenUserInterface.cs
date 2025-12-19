using System;
using System.Linq;
using System.Xml.Linq;
using Assets.Scripts;
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
    }

    private void Start()
    {
        Instance = this;
        Game.Instance.SceneManager.SceneLoaded += OnSceneLoaded;
        Game.Instance.UserInterface.AddBuildUserInterfaceXmlAction(UserInterfaceIds.Flight.NavPanel, OnBuildFlightUI);
    }
    

    private void OnSceneLoaded(object sender, SceneEventArgs e)
    {
        if (e.Scene == "Flight")
        {
            try
            {
                Debug.Log("CreateInspectorPanel from onSceneLoaded ");
                CreateInspectorPanel();
                inspectorPanel.Visible = false;
                inspectorPanel.CloseButtonClicked += OnCloseButtonClicked;
            }
            catch (Exception ex)
            {
                Debug.LogError("Volken: Error creating InspectorPanel: " + ex);
            }
        }
    }
    private void OnCloseButtonClicked(IInspectorPanel inspectorPanel)
    {
        inspectorPanel.Visible = false;
    }
    private static void OnBuildFlightUI(BuildUserInterfaceXmlRequest request)
    {
        var ns = XmlLayoutConstants.XmlNamespace;
        var inspectButton = request.XmlDocument
            .Descendants(ns + "ContentButton")
            .First(x => (string)x.Attribute("id") == "toggle-flight-inspector");
        inspectButton.Parent.Add(
            new XElement(
                ns + "ContentButton",
                new XAttribute("id", volkenUserInterfaceID),
                new XAttribute("class", "panel-button audio-btn-click"),
                new XAttribute("tooltip", "Toggle Volken UI."),
                new XAttribute("name", "NavPanel.OnToggleVolkenUI"),
                new XElement(
                    ns + "Image",
                    new XAttribute("class", "panel-button-icon"),
                    new XAttribute("sprite", "Volken/Sprites/VolkenUI"))));
    }

    //this is called in Harmony
    public void OnToggleVolkenUI()
    {
        Debug.Log("Volken inspector panel clicked");
        UpdateInfo(); 
        try 
        {
            inspectorPanel.Visible = !inspectorPanel.Visible;
        } 
        catch (Exception) 
        {
            CreateInspectorPanel();
            Debug.LogFormat("Creating inspector panel from OnToggleVolkenUI");
            inspectorPanel.Visible = !inspectorPanel.Visible;
            
        }
    }

    private void CreateInspectorPanel()
    {
        Debug.Log("Volken Creating inspector panel");
        inspectorModel = new InspectorModel("VolkenSettingsInspector", "<color=green>Cloud Settings");
        Debug.LogFormat("Created!");
        inspectorModel.Add(new TextModel("test"));
       
        inspectorPanel = Game.Instance.UserInterface.CreateInspectorPanel(inspectorModel, new InspectorPanelCreationInfo()
        {
            PanelWidth = 400,
            Resizable = true,
        });
        Debug.LogFormat("we are good");
    }
    

    
    private void UpdateInfo()
    {
        
    }
   
}