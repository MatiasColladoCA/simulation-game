using Godot;

[GlobalClass]
public partial class PlanetBiomeData : Resource
{
    // Paleta de Colores
    [Export] public Color DeepSeaColor = new Color(0.02f, 0.05f, 0.15f);
    [Export] public Color ShallowColor = new Color(0.1f, 0.4f, 0.45f);
    [Export] public Color BeachColor   = new Color(0.76f, 0.65f, 0.5f);
    [Export] public Color GrassColor   = new Color(0.15f, 0.35f, 0.1f);
    [Export] public Color RockColor    = new Color(0.35f, 0.32f, 0.3f);
    [Export] public Color SnowColor    = new Color(0.95f, 0.95f, 1.0f);
    
    // Propiedades Visuales Físicas
    [Export] public float OceanDepthMultipler = 10.0f; // Metros para oscuridad total
    [Export] public float RoughnessLand = 0.8f;
    [Export] public float RoughnessWater = 0.2f;

    public static PlanetBiomeData GenerateRandom()
    {
        var data = new PlanetBiomeData();
        var rng = new RandomNumberGenerator();
        rng.Randomize();

        // Variación sutil o alienígena
        float alienFactor = rng.Randf(); 

        if (alienFactor > 0.7f) // 30% probabilidad de mundo raro
        {
            // Mundo Alienígena (Ej: Agua Roja, Pasto Azul)
            data.ShallowColor = new Color(rng.Randf(), 0.1f, 0.1f);
            data.GrassColor = new Color(0.1f, 0.1f, rng.Randf());
        }
        else
        {
            // Mundo Terrestre (Variaciones naturales)
            // Variar el azul del agua y el verde del pasto ligeramente
            data.ShallowColor = new Color(0.0f, 0.3f + rng.Randf() * 0.2f, 0.4f + rng.Randf() * 0.2f);
            data.GrassColor = new Color(0.1f + rng.Randf() * 0.1f, 0.3f + rng.Randf() * 0.2f, 0.1f);
        }

        return data;
    }
}