using Godot;
using System;

[GlobalClass]
public partial class SimulationConfig : Resource
{
    [ExportGroup("Simulation Settings")]
    [Export] public int WorldSeed = 1111;
    [Export] public bool RandomizeSeed = true;

    [ExportGroup("Simulation DNA")]
    [Export(PropertyHint.Range, "0,1")] public float GlobalHumidity = 0.5f;
    [Export(PropertyHint.Range, "0,1")] public float GlobalTemperature = 0.5f;

    [ExportGroup("Noise Fine Tuning")]
    [Export] public float WarpStrength = 0.15f;
    [Export] public float DetailFrequency = 4.0f;
    [Export] public float RidgeSharpness = 2.5f;
    [Export(PropertyHint.Range, "0,1")] public float MaskStart = 0.6f;
    [Export(PropertyHint.Range, "0,1")] public float MaskEnd = 0.75f;

    [ExportGroup("Simulation Grid")]
    [Export(PropertyHint.Range, "32,128")] public int GridResolution = 64;
}