using Godot;

public class PlanetChunk
{
    // --- CONFIGURACIÓN ---
    private const int MAX_LOD = 10; // Subir a 8-9 para tamaño real

    // --- DATOS ESTÁTICOS (Contexto del Planeta Actual) ---
    // Configurados por PlanetRender antes de iniciar el Update loop
    public static Mesh BaseMesh; 
    public static Material ChunkMaterial; 
    public static Node3D RootNode; // El contenedor (Visuals)
    public static float Radius; // Renombrado para claridad

    // --- PROPIEDADES DE INSTANCIA ---
    public PlanetChunk Parent;
    public PlanetChunk[] Children;
    public MeshInstance3D MeshInstance;
    
    public Vector3 Center; // Vector dirección normalizado (0 a 1)
    public Vector3 AxisA;
    public Vector3 AxisB;
    public float Size;     // Tamaño relativo (1.0 = cara completa)
    public int LodLevel;

    // Constructor
    public PlanetChunk(PlanetChunk parent, float radius, Vector3 center, Vector3 axisA, Vector3 axisB, float size, int lod)
    {
        Parent = parent;
        Radius = radius;
        Center = center;
        AxisA = axisA;
        AxisB = axisB;
        Size = size;
        LodLevel = lod;

        // GD.Print("ADASDASD");
        // GD.Print(radius);

    }

    // --- UPDATE LOOP (Llamado por PlanetRender) ---
    // AHORA RECIBE LA CÁMARA COMO ARGUMENTO (Sin dependencia global)
    public void Update(Vector3 CameraPos)
    {
        // 1. Calculamos posiciones en espacio Global
        Vector3 spherePos = Center.Normalized() * Radius;
        Vector3 globalChunkPos = RootNode.GlobalTransform * spherePos;
        Vector3 planetCenter = RootNode.GlobalPosition;

        // 2. HORIZON CULLING (La optimización que pides)
        // Vector desde el centro del planeta hacia este chunk
        Vector3 planetToChunkDir = (globalChunkPos - planetCenter).Normalized();
        // Vector desde el centro del planeta hacia la cámara
        Vector3 planetToCamDir = (CameraPos - planetCenter).Normalized();

        // Producto Punto: 1.0 = Frente, 0.0 = Horizonte, -1.0 = Atrás
        float dot = planetToChunkDir.Dot(planetToCamDir);

        // Umbral de corte. 
        // Usamos 0.25f positivo para ser conservadores (que no desaparezcan montañas en el borde).
        // Si es menor que esto, está en el horizonte o atrás -> NO SUBDIVIDIR.
        if (dot < 0.25f)
        {
            // Si tiene hijos, los borramos (ahorramos memoria y draw calls)
            if (Children != null) Merge();
            
            // Opcional: Si está muy atrás, ocultar incluso la malla base
            // if (dot < -0.2f && MeshInstance != null) MeshInstance.Visible = false;
            
            return; // Salimos. No calculamos distancia ni generamos nada más.
        }

        // // 3. DISTANCIA Y LOD (Lógica estándar si pasó el filtro de horizonte)
        // // float dist = globalChunkPos.DistanceTo(CameraPos);

        // float baseDist = globalChunkPos.DistanceTo(CameraPos);

        // // dot ya lo calculaste arriba
        // float angularFactor = Mathf.Clamp(dot, 0.0f, 3.0f);

        // // EXPONENCIAL: castiga MUY fuerte los laterales
        // angularFactor = Mathf.Pow(angularFactor, 3.0f); // 2–4 es buen rango

        // float effectiveDist = baseDist / Mathf.Max(angularFactor, 0.05f);

        
        // // Ajusta este multiplicador (aggressiveness) según tu gusto visual/rendimiento
        
        // float lodMultiplier = 3.0f;// Mathf.Pow(1.0f, LodLevel);
        // // 2.0f es balanceado. 1.5f es más optimizado (pierde calidad antes).
        // float splitDist = (Size * Radius) * 3.0f * lodMultiplier;
        
        // if (effectiveDist < splitDist && LodLevel < MAX_LOD)
        // {
        //     if (Children == null) Split(Radius);
        //     foreach (var child in Children) child.Update(CameraPos);
        // }
        // else
        // {
        //     if (Children != null) Merge();
        //     if (MeshInstance == null) CreateMesh();
        //     MeshInstance.Visible = true;
        // }


        // 3. CÁLCULO DE LOD (Matemática Mejorada)
    float distToCam = globalChunkPos.DistanceTo(CameraPos);

    // Ajuste Angular (Foveated Rendering)
    // Reduce la calidad en los bordes de la visión para ganar rendimiento
    float angularFactor = Mathf.Clamp(dot, 0.0f, 1.0f);
    angularFactor = Mathf.Pow(angularFactor, 3.0f); 
    
    // 'effectiveDist' engaña al sistema: si estás mirando de reojo, 
    // le hace creer que estás más lejos para que baje la calidad.
    float effectiveDist = distToCam / Mathf.Max(angularFactor, 0.1f);

    // --- AQUÍ ESTÁ LA CLAVE DE LOS "3 NIVELES" ---
    
    // Convertimos el tamaño relativo (0.5, 0.25...) a metros reales
    float realSize = Size * Radius;

    // FACTOR DE CALIDAD (QualityFactor)
    // 2.0 = Calidad Baja (Se simplifica rápido)
    // 3.0 = Calidad Media 
    // 4.0 = Calidad Alta (Mantiene el detalle medio mucho más lejos)
    // 5.0 = Ultra (Cuidado con FPS)
    float qualityFactor = 4.0f; 

    // Fórmula simple y robusta:
    // "Divídete si la cámara está más cerca que X veces tu propio tamaño"
    float splitDist = realSize * qualityFactor;

    // 4. DECISIÓN
    // Si estamos dentro del rango de división y no hemos llegado al límite de profundidad
    if (effectiveDist < splitDist && LodLevel < MAX_LOD)
    {
        // --- ZONA CERCA / MEDIA (Requiere más detalle) ---
        if (Children == null) Split(Radius); // Pasar radio si tu Split lo pide
        foreach (var child in Children) child.Update(CameraPos);
    }
    else
    {
        // --- ZONA LEJOS (Se queda como bloque grande) ---
        if (Children != null) Merge();
        if (MeshInstance == null) CreateMesh();
        MeshInstance.Visible = true;
        
        // DEBUG VISUAL: Colorear según "Nivel" percibido
        // Esto te ayudará a ver esas "3 zonas" que buscas
        /*
        if (LodLevel < 2) MeshInstance.Modulate = new Color(1, 0, 0); // ROJO (Lejos)
        else if (LodLevel < 5) MeshInstance.Modulate = new Color(1, 1, 0); // AMARILLO (Medio)
        else MeshInstance.Modulate = new Color(0, 1, 0); // VERDE (Cerca)
        */
    }
    }



    private void Split(float radius)
    {
        // Ocultar malla propia
        if (MeshInstance != null) MeshInstance.Visible = false;

        Children = new PlanetChunk[4];
        float half = Size * 0.5f;
        float quart = Size * 0.25f;

        // Crear 4 hijos (Quadtree)
        // TL, TR, BL, BR
        Children[0] = new PlanetChunk(this, radius, Center - AxisA * quart - AxisB * quart, AxisA, AxisB, half, LodLevel + 1);
        Children[1] = new PlanetChunk(this, radius, Center + AxisA * quart - AxisB * quart, AxisA, AxisB, half, LodLevel + 1);
        Children[2] = new PlanetChunk(this, radius, Center - AxisA * quart + AxisB * quart, AxisA, AxisB, half, LodLevel + 1);
        Children[3] = new PlanetChunk(this, radius, Center + AxisA * quart + AxisB * quart, AxisA, AxisB, half, LodLevel + 1);
    }



    private void Merge()
    {
        if (Children == null) return;
        
        foreach (var child in Children) child.FreeRecursively();
        Children = null;
    }

    private void CreateMesh()
    {
        // Instanciación segura
        if (RootNode == null) return;

        MeshInstance = new MeshInstance3D();
        MeshInstance.Name = $"Chunk_L{LodLevel}_{Center}"; // Nombre único para debug
        MeshInstance.Mesh = BaseMesh;


        
        MeshInstance.MaterialOverride = ChunkMaterial;
        
        // Sombras: En planetas masivos a veces conviene apagarlas en LODs lejanos
        // pero lo dejamos encendido por defecto.
        MeshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;

        // --- INSTANCING SHADER DATA ---
        // Esto envía los datos al 'planet_render.gdshader'
        MeshInstance.SetInstanceShaderParameter("chunk_center", Center);
        MeshInstance.SetInstanceShaderParameter("chunk_axis_a", AxisA);
        MeshInstance.SetInstanceShaderParameter("chunk_axis_b", AxisB);
        MeshInstance.SetInstanceShaderParameter("chunk_size", Size);

        RootNode.AddChild(MeshInstance);
        GD.Print($"[Chunk Debug] Creado visualmente: {MeshInstance.Name} en {Center}");
    }

    public void FreeRecursively()
    {
        // Limpieza profunda para evitar Memory Leaks
        if (Children != null)
        {
            foreach (var child in Children) child.FreeRecursively();
        }
        
        if (MeshInstance != null)
        {
            if (GodotObject.IsInstanceValid(MeshInstance))
            {
                MeshInstance.QueueFree();
            }
            MeshInstance = null;
        }
    }
}