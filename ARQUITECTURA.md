        # Arquitectura del Sistema de Simulación ECS

        ## Visión General

        Este proyecto implementa un sistema de simulación de agentes en tiempo real sobre un planeta generado proceduralmente, utilizando **Godot 4.5** con **C# (.NET 8.0)** y **Compute Shaders** para aprovechar la GPU. El sistema sigue una arquitectura **component-based** orientada a la simulación masiva de entidades.

        ---

        ## 1. Sistema ECS (Entity Component System)

        ### 1.1 Concepto General

        Aunque el proyecto no implementa un ECS puro tradicional, utiliza principios similares:

        - **Entidades**: Los agentes (`AgentDataSphere`) representan entidades individuales
        - **Componentes**: Los datos de cada agente se almacenan en estructuras (`AgentDataSphere` struct)
        - **Sistemas**: Los compute shaders procesan todas las entidades en paralelo en la GPU

        ### 1.2 Estructura de Datos de Agentes

        ```csharp
        [StructLayout(LayoutKind.Sequential, Pack = 16)]
        public struct AgentDataSphere
        {
            public Vector4 Position;   // xyz: posición, w: state_timer
            public Vector4 Velocity;   // xyz: velocidad, w: current_state
            public Vector4 GroupData;  // x: group_id, y: density_time
            public Vector4 Color;      // rgb: color, w: LIFE (0=Muerto, 1=Vivo)
        }
        ```

        **Características clave:**
        - Alineación de 16 bytes para compatibilidad con GPU
        - Empaquetado eficiente para transferencias CPU↔GPU
        - Estado de vida codificado en `Color.w`
        - Timer de estado en `Position.w`

        ### 1.3 Pipeline de Simulación

        El sistema ejecuta **4 fases** en cada frame:

        #### Fase 0: CLEAR
        - Limpia la grilla de densidad 3D (64×64×64 celdas)
        - Prepara el espacio para el conteo de agentes

        #### Fase 1: POPULATE
        - Cuenta agentes vivos por celda espacial
        - Usa operaciones atómicas (`atomicAdd`) para concurrencia segura
        - Genera un mapa de densidad para comportamiento grupal

        #### Fase 2: UPDATE (Fase Principal)
        - **Movimiento**: Combina vector field del planeta + influencia de POIs
        - **Física**: Aceleración, velocidad, fricción
        - **Snapping**: Ajusta agentes a la superficie del terreno usando height map
        - **Estados**: Máquina de estados simple (IDLE, MOVING, WORK)
        - **Muerte**: Detecta condiciones de muerte y recicla índices
        - **Visualización**: Escribe posiciones y colores en texturas 2D

        #### Fase 3: PAINT POIs
        - Genera mapa de influencia 3D desde puntos de interés (POIs)
        - Usa despacho 3D para cubrir todo el volumen
        - Calcula influencia suave con `smoothstep`

        ### 1.4 Almacenamiento de Datos

        **En GPU:**
        - `AgentBuffer`: Storage buffer con todos los agentes (SSBO)
        - `GridBuffer`: Grilla espacial para densidad (64³ celdas)
        - `pos_texture`: Textura 2D RGBA32F con posiciones (2048×N)
        - `col_texture`: Textura 2D RGBA32F con colores/estados
        - `density_texture_out`: Textura 3D R8 con influencia de POIs

        **En CPU:**
        - `_cpuAgents[]`: Array de respaldo para spawn/edición manual
        - Solo se sincroniza cuando es necesario (spawn, debug)

        ---

        ## 2. Dependencias en C#

        ### 2.1 Framework y Versión

        ```xml
        <Project Sdk="Godot.NET.Sdk/4.5.1">
        <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
        </PropertyGroup>
        </Project>
        ```

        **Dependencias principales:**
        - **Godot.NET.Sdk 4.5.1**: SDK oficial de Godot para C#
        - **.NET 8.0**: Runtime de C# (net9.0 para Android)
        - **Unsafe blocks**: Habilitado para manipulación directa de memoria (marshalling GPU)

        ### 2.2 APIs de Godot Utilizadas

        #### RenderingDevice API
        ```csharp
        RenderingDevice _rd = RenderingServer.GetRenderingDevice();
        ```
        - **Propósito**: Acceso de bajo nivel a la GPU (Vulkan/Metal/DirectX)
        - **Uso**: Creación de buffers, texturas, pipelines de compute
        - **Requisito**: Forward+ rendering habilitado

        #### Tipos de Datos GPU
        - `Rid`: Resource ID (identificador de recursos GPU)
        - `RDShaderFile`: Archivo de shader compilado (.glsl)
        - `RDUniform`: Descriptor de uniform/shader binding
        - `RDTextureFormat`: Configuración de texturas
        - `StorageBuffer`: Buffer de almacenamiento (SSBO)

        ### 2.3 Estructuras de Datos Críticas

        #### PlanetParamsData
        ```csharp
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct PlanetParamsData
        {
            // Noise settings (vec4)
            public float NoiseScale, NoiseHeight, WarpStrength, MountainRoughness;
            
            // Curve params (vec4)
            public float OceanFloorLevel, WeightMultiplier, GroundDetailFreq;
            
            // Global offset (vec4)
            public Vector3 NoiseOffset;
            public float PlanetSeed;
            
            // Detail params (vec4)
            public float DetailFrequency, RidgeSharpness, MaskStart, MaskEnd;
            
            // Resolution & Radius (vec4)
            public float ResolutionF, Radius;
        }
        ```

        **Nota**: El `StructLayout` es crítico para alineación con shaders GLSL.

        ---

        ## 3. Estructura del Juego en Godot

        ### 3.1 Jerarquía de Escenas

        ```
        main.tscn (Escena Principal)
        ├── Main.cs (Controlador Principal)
        │   ├── AgentSystem (Node3D)
        │   │   ├── AgentVisualizer (MultiMeshInstance3D)
        │   │   └── Compute Shader: agent_simulation.glsl
        │   ├── AgentRender (MultiMeshInstance3D)
        │   │   └── Shader Visual: agent_render.gdshader
        │   ├── SimulationUI (UI)
        │   └── Planet (Node3D) [Instanciado desde Prefab]
        │       ├── PlanetBaker (Node)
        │       │   └── Compute Shader: planet_baker.glsl
        │       ├── PlanetRender (Node3D)
        │       │   ├── PlanetChunk[] (Quadtree LOD)
        │       │   └── Shader Visual: planet_render.gdshader
        │       └── EnvironmentManager (Node)
        │           ├── POI Buffer (Storage Buffer)
        │           └── Visual POIs (Instancias)
        └── OrbitalCamera (Camera3D)
        ```

        ### 3.2 Flujo de Inicialización

        #### Paso 1: _Ready() en Main.cs
        ```csharp
        _rd = RenderingServer.GetRenderingDevice(); // Validar GPU disponible
        SpawnWorld(); // Crear planeta inicial
        ```

        #### Paso 2: SpawnWorld()
        1. **Generar Configuración**: `GeneratePlanetConfig(seed)`
        2. **Instanciar Planeta**: `PlanetPrefab.Instantiate<Planet>()`
        3. **Inicializar Planeta**: `planet.Initialize(_rd, config, painter)`
        - `PlanetBaker.Bake()` → Genera height map, vector field, normal map
        - `PlanetRender.Initialize()` → Configura quadtree y materiales
        - `EnvironmentManager.Initialize()` → Crea POIs y buffers
        4. **Conectar Agentes**: `SetupAgents(planet)`
        - `AgentSystem.Initialize()` → Configura compute shader
        - `AgentRender.Initialize()` → Configura visualización

        #### Paso 3: Loop de Simulación (_Process)
        ```csharp
        AgentCompute.UpdateSimulation(delta, time); // Ejecuta 4 fases GPU
        UI.UpdateStats(delta, ActiveAgentCount);      // Actualiza UI
        ```

        ### 3.3 Componentes Principales

        #### Main.cs
        - **Rol**: Orquestador principal, punto de entrada
        - **Responsabilidades**:
        - Gestión del ciclo de vida del mundo
        - Coordinación entre sistemas
        - Input handling (spawn de agentes con mouse)
        - Generación procedural de configuraciones

        #### Planet.cs
        - **Rol**: Contenedor del mundo, gestor de recursos
        - **Responsabilidades**:
        - Propiedad de texturas GPU (height map, vector field)
        - Delegación a subsistemas (Baker, Render, Environment)
        - API pública para raycasting y consultas

        #### PlanetBaker.cs
        - **Rol**: Generador procedural de terreno
        - **Responsabilidades**:
        - Ejecuta compute shader de generación
        - Crea cubemaps (6 caras) de altura y vectores
        - Calcula estadísticas (min/max height)
        - Retorna `BakeResult` con RIDs de texturas

        #### AgentSystem.cs
        - **Rol**: Simulador de agentes en GPU
        - **Responsabilidades**:
        - Gestión de buffers y texturas GPU
        - Despacho de compute shaders (4 fases)
        - Sincronización CPU↔GPU
        - API de spawn/gestión de agentes

        #### EnvironmentManager.cs
        - **Rol**: Gestor de entorno y POIs
        - **Responsabilidades**:
        - Creación de buffer de POIs (16 puntos máximo)
        - Visualización de POIs en escena
        - Exposición de recursos para agentes

        #### PlanetRender.cs
        - **Rol**: Renderizador de terreno con LOD
        - **Responsabilidades**:
        - Quadtree adaptativo basado en cámara
        - Gestión de chunks de terreno
        - Aplicación de materiales y texturas
        - Renderizado de agua (esfera)

        ### 3.4 Sistema de Chunks (LOD)

        **PlanetChunk.cs** implementa un quadtree recursivo:

        - **Nivel 0**: 6 caras raíz del cubo
        - **Subdivisión**: Basada en distancia a cámara
        - **Criterio**: Si distancia < threshold → subdividir
        - **Límite**: Máximo de niveles para evitar sobrecarga

        **Ventajas:**
        - Renderiza solo lo visible
        - Reduce polígonos en distancia
        - Actualización asíncrona (cada 100ms)

        ---

        ## 4. Shaders y GPU Compute

        ### 4.1 Compute Shaders

        #### agent_simulation.glsl
        - **Local Size**: 64 threads por grupo
        - **Fases**: 4 fases ejecutadas secuencialmente
        - **Bindings**:
        - `binding 0`: AgentBuffer (SSBO)
        - `binding 1`: GridBuffer (SSBO)
        - `binding 2-3`: Texturas de salida (image2D)
        - `binding 4-5`: Height map y Vector field (samplerCube)
        - `binding 6`: Density texture 3D (image3D)
        - `binding 7`: Counter buffer (SSBO)
        - `binding 8`: POI buffer (SSBO)

        #### planet_baker.glsl
        - **Propósito**: Generación procedural de terreno
        - **Salidas**: Cubemaps de altura, normales, vector field
        - **Técnica**: Domain warping + ridge noise

        ### 4.2 Shaders Visuales

        #### agent_render.gdshader
        - **Tipo**: Fragment shader para instancias
        - **Input**: Texturas de posición y color desde GPU
        - **Técnica**: Billboarding orientado a cámara

        #### planet_render.gdshader
        - **Tipo**: Vertex + Fragment shader
        - **Input**: Height map cubemap, normal map
        - **Técnica**: Displacement mapping esférico

        ---

        ## 5. Patrones de Diseño Utilizados

        ### 5.1 Dependency Injection
        - `Main.cs` inyecta `RenderingDevice` a todos los sistemas
        - `Planet` inyecta texturas a `AgentSystem` y `EnvironmentManager`
        - Reduce acoplamiento entre componentes

        ### 5.2 Factory Pattern
        - `Main.cs` actúa como fábrica de planetas
        - `GeneratePlanetConfig()` crea configuraciones procedurales
        - `PlanetPrefab.Instantiate()` crea instancias

        ### 5.3 Component-Based Architecture
        - Cada sistema es un `Node` independiente
        - Comunicación mediante referencias exportadas (`[Export]`)
        - Separación clara de responsabilidades

        ### 5.4 Object Pooling (GPU)
        - Agentes muertos se reciclan mediante stack de índices libres
        - `DeadListBuffer` almacena índices reutilizables
        - Evita fragmentación de memoria GPU

        ---

        ## 6. Flujo de Datos GPU

        ### 6.1 Escritura
        ```
        CPU (C#) → Marshal → Byte Array → StorageBuffer → GPU
        ```

        ### 6.2 Lectura
        ```
        GPU → StorageBuffer → Byte Array → Unmarshal → CPU (C#)
        ```

        ### 6.3 Sincronización
        - **Implícita**: `ComputeListEnd()` + lectura de contador
        - **Explícita**: `rd.Sync()` (solo en Baker, bloqueante)

        ---

        ## 7. Configuración y Parámetros

        ### 7.1 Constantes Críticas

        ```csharp
        // AgentSystem.cs
        const int DATA_TEX_WIDTH = 2048;      // Ancho de textura de datos
        const int GRID_RES = 64;              // Resolución de grilla 3D
        const int GRID_TOTAL_CELLS = 262144;   // 64³

        // PlanetBaker.cs
        const float FIXED_POINT_SCALE = 100000.0f; // Escala para estadísticas
        ```

        ### 7.2 Parámetros Exportables

        **En Main.cs (Inspector):**
        - `AgentCount`: Número de agentes (default: 5000)
        - `WorldSeed`: Semilla de generación
        - `NoiseScale`, `NoiseHeight`: Parámetros de terreno
        - `WarpStrength`, `DetailFrequency`: Fine-tuning de ruido

        ---

        ## 8. Limitaciones y Consideraciones

        ### 8.1 Limitaciones Actuales
        - **POIs**: Máximo 16 puntos de interés (hardcoded en shader)
        - **Agentes**: Limitado por tamaño de textura (2048×N)
        - **Grilla**: Resolución fija de 64×64×64
        - **Thread Groups**: Tamaño fijo de 64 threads

        ### 8.2 Optimizaciones Futuras
        - Culling de agentes fuera de vista
        - Frustum culling en GPU
        - Spatial hashing dinámico
        - Multi-threading CPU para tareas auxiliares

        ---

        ## 9. Estructura de Archivos

        ```
        simulacion-ecs/
        ├── Scripts/
        │   ├── Main.cs                    # Controlador principal
        │   ├── Agents/
        │   │   ├── AgentSystem.cs         # Simulador GPU
        │   │   └── AgentRender.cs         # Renderizador visual
        │   ├── Planet/
        │   │   ├── Planet.cs              # Contenedor del mundo
        │   │   ├── PlanetBaker.cs         # Generador procedural
        │   │   ├── PlanetRender.cs        # Renderizador con LOD
        │   │   ├── PlanetChunk.cs         # Chunk del quadtree
        │   │   ├── EnvironmentManager.cs  # Gestor de entorno
        │   │   └── Structs/
        │   │       └── PlanetParamsData.cs # Estructura de datos
        │   └── Utils/
        │       └── TerrainNoise.cs        # Utilidades de ruido
        ├── Shaders/
        │   ├── Compute/
        │   │   ├── agent_simulation.glsl  # Shader principal de agentes
        │   │   └── planet_baker.glsl      # Shader de generación
        │   └── Visual/
        │       ├── agent_render.gdshader  # Shader visual de agentes
        │       └── planet_render.gdshader # Shader visual de terreno
        ├── Scenes/
        │   └── Prefabs/
        │       └── Planet.tscn            # Prefab del planeta
        └── simulacion_ecs.csproj          # Proyecto C#
        ```

        ---

        ## 10. Conclusión

        Este proyecto demuestra una arquitectura moderna de simulación masiva utilizando:
        - **GPU Compute** para procesamiento paralelo
        - **Component-Based Design** para modularidad
        - **Procedural Generation** para mundos infinitos
        - **LOD System** para renderizado eficiente

        La separación clara entre sistemas permite escalabilidad y mantenibilidad, mientras que el uso de compute shaders garantiza rendimiento para miles de agentes simultáneos.











```
┌─────────────────────────────────────────────────────────────┐
│                    ARQUITECTURA AAA                          │
├─────────────────────────────────────────────────────────────┤
│                                                               │
│  Main.cs (Orquestador)                                        │
│    │                                                          │
│    ├─→ Planet.Initialize()                                   │
│    │     │                                                    │
│    │     ├─→ PlanetBaker.Bake()                              │
│    │     │     └─→ Genera: HeightMap, VectorField, NormalMap │
│    │     │                                                    │
│    │     ├─→ EnvironmentManager.Initialize()                 │
│    │     │     ├─→ SetupPoiBuffer()                          │
│    │     │     ├─→ CreateInfluenceTexture()  ← NUEVO         │
│    │     │     └─→ CreateVisualPOIs()                        │
│    │     │                                                    │
│    │     └─→ PlanetRender.Initialize()                       │
│    │                                                          │
│    └─→ SetupAgents(planet)                                    │
│          │                                                     │
│          ├─→ AgentSystem.Initialize()                         │
│          │     ├─→ Recibe: HeightMap, VectorField            │
│          │     ├─→ Recibe: POIBuffer, InfluenceTexture ← NUEVO│
│          │     ├─→ Crea: AgentBuffer, GridBuffer              │
│          │     ├─→ Crea: PosTexture, ColorTexture             │
│          │     └─→ Usa: InfluenceTexture (no la crea) ← CAMBIO│
│          │                                                     │
│          └─→ AgentRender.Initialize()                         │
│                └─→ Recibe: PosTextureRid, ColorTextureRid     │
│                                                               │
└─────────────────────────────────────────────────────────────┘
```