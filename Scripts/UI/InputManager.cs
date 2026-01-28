using Godot;
using System;

public partial class InputManager : Node
{
    // Eventos (Intenciones abstractas)
    public event Action OnToggleConsole;
    public event Action OnResetSimulation;
    public event Action<Vector2> OnSpawnAgentRequest; // Enviamos posición del mouse
    public event Action<bool> OnCtrlKeyChanged; // Para saber si estamos en modo spawn

    public override void _UnhandledInput(InputEvent @event)
    {
        // 1. Toggle Console
        if (@event.IsActionPressed("toggle_console")) 
        {
            OnToggleConsole?.Invoke();
        }

        // 2. Reset
        if (@event is InputEventKey k && k.Pressed && k.Keycode == Key.R)
        {
            OnResetSimulation?.Invoke();
        }

        // 3. Spawn Request (Click Izquierdo + Ctrl)
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            if (Input.IsKeyPressed(Key.Ctrl))
            {
                OnSpawnAgentRequest?.Invoke(mb.Position);
            }
        }
    }
}