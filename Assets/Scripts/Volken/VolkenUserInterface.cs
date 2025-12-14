using System;
using UnityEngine;

public class VolkenUserInterface:MonoBehaviour
{
    public static VolkenUserInterface Instance;

    private void Awake()
    {
        Instance = this;
    }
}