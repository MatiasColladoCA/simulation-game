using Godot;
using System;

public partial class AgentDirector : Node
{
    // --- DEPENDENCIAS ---
    [ExportGroup("Sistemas Internos")]
    [Export] public AgentSystem ComputeSystem; // El cerebro (GPU)
    [Export] public AgentRender RenderSystem;  // El cuerpo (Visuales)
    [Export] public SimulationUI UI;           // El reporte

    // --- ESTADO ---
    private Planet _currentPlanet;
    private int _nextSpawnIndex = 40000; // Lógica movida desde Main

    // ---------------------------------------------------------
    // 1. INICIALIZACIÓN (Setup)
    // ---------------------------------------------------------
    public void OnWorldCreated(RenderingDevice rd, Planet planet, int gridResolution)
    {
        _currentPlanet = planet;

        // A. Validaciones defensivas
        // var env = planet.Env;
        // if (env == null)
        // {
        //     GD.PrintErr("[AgentDirector] Environment no listo. Abortando setup de agentes.");
        //     return;
        // }

        // B. Inicializar Cómputo (GPU)
        ComputeSystem.Initialize(rd, planet, planet.GetParams(), gridResolution);
        
        if (!ComputeSystem.IsInitialized)
        {
            GD.PrintErr("[AgentDirector] Falló init de ComputeSystem.");
            return;
        }

        // C. Inicializar Render (Visuales)
        // Aquí encapsulamos la lógica "sucia" de reparenting
        SetupVisuals(planet);

        GD.Print($"[AgentDirector] Sistema listo. {_nextSpawnIndex} agentes en espera.");
    }

    private void SetupVisuals(Planet planet)
    {
        // Obtener RIDs del sistema de cómputo
        var posRid = ComputeSystem.GetPosTextureRid();
        var colRid = ComputeSystem.GetColorTextureRid();

        // Lógica de Reparenting (Movemos el renderizador para que viaje con el planeta)
        RenderSystem.GetParent()?.RemoveChild(RenderSystem);
        planet.AddChild(RenderSystem);
        
        // Reset Transform local
        RenderSystem.Position = Vector3.Zero;
        RenderSystem.Rotation = Vector3.Zero;
        RenderSystem.Scale = Vector3.One;

        // Inicializar Render
        RenderSystem.Initialize(posRid, colRid, ComputeSystem.AgentCount);
    }

    // ---------------------------------------------------------
    // 2. GAME LOOP (Process)
    // ---------------------------------------------------------
    public void OnProcess(double delta, double time)
    {
        if (ComputeSystem != null && ComputeSystem.IsInitialized)
        {
            // Update Simulación
            ComputeSystem.UpdateSimulation(delta, time);

            // Update UI
            if (UI != null) 
                UI.UpdateStats(delta, (int)ComputeSystem.ActiveAgentCount);
        }
    }

    // ---------------------------------------------------------
    // 3. INTERACCIÓN (Spawn)
    // ---------------------------------------------------------
    public void TrySpawnAgent(Camera3D camera, Vector2 mousePos)
    {
        if (_currentPlanet == null) return;

        // Raycast logic delegada al Planeta, pero coordinada aquí
        Vector3 rayOrigin = camera.ProjectRayOrigin(mousePos);
        Vector3 rayDir = camera.ProjectRayNormal(mousePos);

        if (_currentPlanet.RaycastHit(rayOrigin, rayDir, out Vector3 hitPoint))
        {
            Vector3 localHit = _currentPlanet.ToLocal(hitPoint);

            // Spawn real
            ComputeSystem.SpawnAgent(localHit, _nextSpawnIndex);
            
            // Ciclo de índices
            _nextSpawnIndex = 4000 + ((_nextSpawnIndex - 4000 + 1) % 1000);
            
            GD.Print($"[AgentDirector] Agente desplegado en: {localHit}");
        }
    }

    // --- LÓGICA DE GAMEPLAY (Movida desde AgentSystem) ---

    public void SpawnInitialPopulation(int count, float planetRadius)
    {
        if (ComputeSystem == null || !ComputeSystem.IsInitialized) return;

        var rng = new RandomNumberGenerator();
        rng.Randomize();
        
        GD.Print($"[AgentDirector] Iniciando colonización: {count} agentes.");

        int spawnedCount = 0;
        int maxAgents = ComputeSystem.AgentCount; // Necesitas exponer esto o usar una constante

        // Nota: Si AgentSystem no expone 'AgentCount' público, hazlo público o usa una constante.
        
        for (int i = 0; i < maxAgents; i++)
        {
            if (spawnedCount >= count) break;

            // Lógica de Distribución (Matemática pura, vive en el Director)
            float phi = rng.Randf() * Mathf.Tau;
            float cosTheta = rng.RandfRange(-1.0f, 1.0f);
            float theta = Mathf.Acos(cosTheta);

            Vector3 randomDir = new Vector3(
                Mathf.Sin(theta) * Mathf.Cos(phi),
                Mathf.Sin(theta) * Mathf.Sin(phi),
                Mathf.Cos(theta)
            );

            Vector3 spawnPos = randomDir * planetRadius;

            // Orden Ejecutiva: "Sistema, coloca un agente en el índice i en la pos X"
            ComputeSystem.SpawnAgent(spawnPos, i);
            
            spawnedCount++;
        }
    }

    public void ClearAgents()
    {
        GD.Print("Implementar ClearAgents");
    }
}