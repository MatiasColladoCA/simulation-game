using Godot;
using System.Threading.Tasks;

public partial class SimulationController : Node
{
	// Dependencias de Escena (Arrastrar en Editor)
	[Export] public TerrainBaker Baker;
	[Export] public AgentSystem AgentSys;
	[Export] public PlanetRender PlanetRenderer; // Tu script existente
	[Export] public SimulationUI UI;

	private RenderingDevice _rd;
	private bool _isRunning = false;

	public override async void _Ready()
	{
		_rd = RenderingServer.GetRenderingDevice();
		
		// 1. Validar dependencias
		if (Baker == null || AgentSys == null || PlanetRenderer == null || UI == null)
		{
			GD.PrintErr("SimulationController: Faltan nodos asignados en el Inspector.");
			return;
		}

		GD.Print("Iniciando Staggered Loading...");

		// 2. BAKE (Frame 1)
		var bakeResult = Baker.Bake(_rd);
		if (!bakeResult.Success) return;
		
		await ToSignal(GetTree(), "process_frame");
		await ToSignal(GetTree(), "process_frame"); // Espera seguridad GPU

		// 3. AGENTES (Frame 3)
		// Pasamos los parámetros físicos del Baker al Sistema de Agentes
		AgentSys.Initialize(
			_rd, 
			bakeResult.HeightMapRid, 
			bakeResult.VectorFieldRid,
			Baker.PlanetRadius,
			Baker.NoiseScale,
			Baker.NoiseHeight
		);

		await ToSignal(GetTree(), "process_frame");

		// 4. PLANETA (Frame 4)
		// PlanetRender necesita inicializarse con las texturas horneadas
		PlanetRenderer.Initialize(
			bakeResult.HeightMapRid, 
			bakeResult.VectorFieldRid, 
			Baker.PlanetRadius, 
			Baker.NoiseHeight
		);

		_isRunning = true;
		GD.Print("Simulation Loop Started.");
	}

	public override void _Process(double delta)
	{
		if (!_isRunning) return;

		double time = Time.GetTicksMsec() / 1000.0;
		
		// Ejecutar simulación
		AgentSys.UpdateSimulation(delta, time);
		
		// Actualizar UI
		UI.UpdateStats(delta, AgentSys.AgentCount);
	}
	
	// Limpieza global si es necesaria, usualmente los hijos limpian sus propios RIDs
}
