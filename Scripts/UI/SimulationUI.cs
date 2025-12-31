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
}
