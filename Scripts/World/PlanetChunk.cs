using Godot;

public class PlanetChunk
{

    // --- SEGURIDAD ---
    // Bajamos de 8 a 5. (4^5 = 1024 chunks max por cara, seguro)
    // Cuando funcione, subimos a 6 o 7. 8 es para planetas escala real.
    private const int MAX_LOD = 2;
    // Datos compartidos por todos los chunks (Static)
    public static Mesh BaseMesh; 
    public static Material ChunkMaterial; 
    public static Node3D RootNode; // El nodo padre en la escena
    public static float Radius;
    public static Vector3 CameraPos;
    
    // Propiedades de la Instancia
    public PlanetChunk Parent;
    public PlanetChunk[] Children;
    public MeshInstance3D MeshInstance;
    
    public Vector3 Center; // Posición en el cubo
    public Vector3 AxisA;
    public Vector3 AxisB;
    public float Size;     // Tamaño relativo (1.0 = cara completa)
    public int LodLevel;

    public PlanetChunk(PlanetChunk parent, Vector3 center, Vector3 axisA, Vector3 axisB, float size, int lod)
    {
        Parent = parent;
        Center = center;
        AxisA = axisA;
        AxisB = axisB;
        Size = size;
        LodLevel = lod;
    }

public void Update()
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

        // 3. DISTANCIA Y LOD (Lógica estándar si pasó el filtro de horizonte)
        float dist = globalChunkPos.DistanceTo(CameraPos);
        
        // Ajusta este multiplicador (aggressiveness) según tu gusto visual/rendimiento
        // 2.0f es balanceado. 1.5f es más optimizado (pierde calidad antes).
        float splitDist = (Size * Radius) * 1.5f; 

        if (dist < splitDist && LodLevel < MAX_LOD) 
        {
            if (Children == null) Split();
            foreach (var child in Children) child.Update();
        }
        else
        {
            if (Children != null) Merge();
            
            if (MeshInstance == null) CreateMesh();
            MeshInstance.Visible = true;
        }
    }



    private void Split()
    {
        // Ocultar malla propia
        if (MeshInstance != null) MeshInstance.Visible = false;

        Children = new PlanetChunk[4];
        float half = Size * 0.5f;
        float quart = Size * 0.25f;

        // Crear 4 hijos (Quadtree)
        // TL, TR, BL, BR
        Children[0] = new PlanetChunk(this, Center - AxisA * quart - AxisB * quart, AxisA, AxisB, half, LodLevel + 1);
        Children[1] = new PlanetChunk(this, Center + AxisA * quart - AxisB * quart, AxisA, AxisB, half, LodLevel + 1);
        Children[2] = new PlanetChunk(this, Center - AxisA * quart + AxisB * quart, AxisA, AxisB, half, LodLevel + 1);
        Children[3] = new PlanetChunk(this, Center + AxisA * quart + AxisB * quart, AxisA, AxisB, half, LodLevel + 1);
    }

    private void Merge()
    {
        if (Children == null) return;
        
        // Destruir hijos recursivamente
        foreach (var child in Children) child.FreeRecursively();
        Children = null;
    }

    private void CreateMesh()
    {
        MeshInstance = new MeshInstance3D();
        MeshInstance.Name = $"Chunk_L{LodLevel}";
        MeshInstance.Mesh = BaseMesh;
        
        MeshInstance.MaterialOverride = ChunkMaterial;
        MeshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
        
        // --- AQUÍ ESTÁ LA MAGIA ---
        // Pasamos los datos geométricos al shader
        MeshInstance.SetInstanceShaderParameter("chunk_center", Center);
        MeshInstance.SetInstanceShaderParameter("chunk_axis_a", AxisA);
        MeshInstance.SetInstanceShaderParameter("chunk_axis_b", AxisB);
        MeshInstance.SetInstanceShaderParameter("chunk_size", Size);

        RootNode.AddChild(MeshInstance);
    }

    public void FreeRecursively()
    {
        if (Children != null)
        {
            foreach (var child in Children) child.FreeRecursively();
        }
        
        if (MeshInstance != null)
        {
            MeshInstance.QueueFree();
            MeshInstance = null;
        }
    }
}