using Godot;
using System;

public partial class WorldBuilder : Node
{
    // --- RECURSOS (Blueprints) ---
    [ExportGroup("Prefabs")]
    [Export] public PackedScene PlanetPrefab; // El nodo base del planeta

    // --- ESTADO INTERNO (Solo para generación en serie) ---
    private int _planetIndex = 0;

    /// <summary>
    /// Construye un nuevo planeta desde cero.
    /// Fase: OFFLINE (No debe haber nodos en el árbol todavía).
    /// </summary>
    /// <param name="rd">El dispositivo de renderizado global.</param>
    /// <param name="config">La configuración de alto nivel de la simulación.</param>
    /// <param name="bakerShader">El RID del Compute Shader compilado para generar terreno.</param>
    /// <returns>La instancia del planeta inicializada o null si falló.</returns>
    public Planet BuildWorld(RenderingDevice rd, SimulationConfig config, Rid bakerShader)
    {
        // 1. VALIDACIONES
        if (PlanetPrefab == null)
        {
            GD.PrintErr("[WorldBuilder] Error: Falta asignar 'PlanetPrefab' en el Inspector.");
            return null;
        }

        // 2. PREPARACIÓN DE CONFIGURACIÓN (Data Mapping)
        // Convertimos la Config de alto nivel (Resource) en datos crudos para la GPU (Struct).
        // Usamos una semilla única combinando la global con el índice del planeta.
        int currentSeed = config.RandomizeSeed ? (int)DateTime.Now.Ticks : config.WorldSeed;
        int uniqueSeed = HashCode.Combine(currentSeed, _planetIndex++);
        
        PlanetParamsData planetData = MapConfigToStruct(config, uniqueSeed);

        // 3. INSTANCIACIÓN (Memoria)
        // Creamos el objeto, pero aún es "tonto".
        var planetNode = PlanetPrefab.Instantiate<Planet>();
        planetNode.Name = $"Planet_{uniqueSeed}";

        // 4. INICIALIZACIÓN (Baking & Data Allocation)
        // Aquí ocurre la magia. Pasamos el Shader, no una clase Baker instanciada.
        // El Planeta usará este shader internamente para generar sus datos.
        bool success = planetNode.Initialize(rd, planetData, bakerShader);

        if (!success)
        {
            GD.PrintErr("[WorldBuilder] Falló la inicialización interna del planeta.");
            planetNode.QueueFree();
            return null;
        }

        // 5. ENTREGA
        return planetNode;
    }

    /// <summary>
    /// Traduce la configuración del Editor (Resource) a la Estructura de GPU (Struct).
    /// </summary>
    // En WorldBuilder.cs

    private PlanetParamsData MapConfigToStruct(SimulationConfig cfg, int uniqueSeed)
    {
        // Generamos un offset aleatorio basado en la semilla para que el ruido cambie
        Vector3 noiseOffset = GenerateRandomOffset(uniqueSeed);

        return new PlanetParamsData
        {
            // --- BLOQUE 1: NOISE SETTINGS ---
            NoiseScale = cfg.NoiseScale,
            NoiseHeight = cfg.NoiseHeight,
            WarpStrength = cfg.WarpStrength,
            MountainRoughness = cfg.MountainRoughness,

            // --- BLOQUE 2: CURVE PARAMS ---
            OceanFloorLevel = cfg.OceanLevel,
            WeightMultiplier = 2.5f,        // Valor interno (o agrégalo al Config si quieres)
            GroundDetailFreq = 4.0f,        // Valor interno
            _padding2 = 0,                  // Basura para alineación

            // --- BLOQUE 3: GLOBAL OFFSET ---
            NoiseOffset = noiseOffset,
            PlanetSeed = (float)uniqueSeed, // Casteamos a float porque el shader espera float

            // --- BLOQUE 4: DETAIL PARAMS ---
            DetailFrequency = cfg.DetailFrequency,
            RidgeSharpness = cfg.RidgeSharpness,
            MaskStart = cfg.MaskStart,
            MaskEnd = cfg.MaskEnd,

            // --- BLOQUE 5: RES OFFSET ---
            TextureResolution = (float)cfg.TextureResolution, // La resolución física de la textura
            Radius = cfg.Radius,
            LogicResolution = (float)cfg.LogicResolution,
            _padding5 = 0,

            // --- BLOQUE 6: PAD UV ---
            _padding6 = 0, _padding7 = 0, _padding8 = 0, _padding9 = 0
        };
    }

    private Vector3 GenerateRandomOffset(int seed)
    {
        var rng = new Random(seed);
        // Un offset grande evita simetrías en el ruido de Perlin/Simplex
        float range = 10000.0f;
        return new Vector3(
            (float)(rng.NextDouble() * range * 2 - range),
            (float)(rng.NextDouble() * range * 2 - range),
            (float)(rng.NextDouble() * range * 2 - range)
        );
    }

}