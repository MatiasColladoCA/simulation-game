using Godot;

public partial class SimulationUI : CanvasLayer
{
	private Label _statsLabel;
	private double _timeSinceLastUpdate = 0;

	public override void _Ready()
	{
		_statsLabel = new Label();
		_statsLabel.Position = new Vector2(10, 10);
		_statsLabel.LabelSettings = new LabelSettings { 
			FontSize = 24, 
			OutlineSize = 4, 
			OutlineColor = Colors.Black 
		};
		AddChild(_statsLabel);
	}

	public void UpdateStats(double delta, int agentCount)
	{
		_timeSinceLastUpdate += delta;
		if (_timeSinceLastUpdate > 0.25) // 4Hz
		{
			_statsLabel.Text = $"AGENTS: {agentCount}\nFPS: {Engine.GetFramesPerSecond()}";
			_timeSinceLastUpdate = 0;
		}
	}

	
	[Export] private LineEdit _consoleInput; // Asignado en Inspector

	// Devuelve 'true' si la consola quedó abierta, 'false' si se cerró
	public bool ToggleConsole()
	{
		if (_consoleInput == null) return false;

		bool isVisible = !_consoleInput.Visible;
		_consoleInput.Visible = isVisible;

		if (isVisible)
		{
			_consoleInput.GrabFocus();
		}
		else
		{
			_consoleInput.ReleaseFocus();
			_consoleInput.Text = ""; // Limpiar al cerrar
		}

		return isVisible;
	}
	
	// Opcional: Para leer comandos desde Main
	public string GetConsoleText() => _consoleInput?.Text ?? "";
}
