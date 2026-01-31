# 🌍 Planetary Sociological Simulation Engine (Research Prototype)

![Godot Engine](https://img.shields.io/badge/GODOT_4.x-.NET-blue?style=for-the-badge&logo=godot-engine) ![C#](https://img.shields.io/badge/C%23-10.0-green?style=for-the-badge&logo=c-sharp) ![GLSL](https://img.shields.io/badge/GLSL-Compute-orange?style=for-the-badge&logo=opengl) ![Architecture](https://img.shields.io/badge/Arch-Hybrid_ECS-purple?style=for-the-badge)

> **A high-performance, GPU-driven planetary simulation exploring the intersection of emergent sociology, non-linear dynamics, and massive agent-based modeling.**

## 📖 Abstract

This project serves as a technical research laboratory designed to simulate complex social systems on an interplanetary scale. Moving beyond traditional game loops, it implements a **Data-Oriented Hybrid Architecture** to handle procedural habitable worlds, atmospheric physics, and massive agent populations (targeting 500k+ entities) entirely on the GPU.

The simulation acts as a "Petri dish" where the environment (Terrain, Climate) and agents interact through strictly defined **Knowledge Fields**, observing how individual decisions governed by Game Theory and Biological Traits aggregate into macroscopic social structures and emergent civilizations.

---

## ⚙️ Technical Architecture

To achieve real-time performance with massive datasets, this project abandons standard Object-Oriented patterns in favor of a **Data-Oriented Design (DOD)** approach, integrated within Godot's node system.

### 1. Hybrid ECS (Entity-Component-System)
The architecture utilize a hybrid approach that leverages Godot for the Scene Tree hierarchy but offloads all heavy logic to pure C# structs and GPU Compute Shaders.
* **Entities:** Lightweight Nodes (`Planet`, `AgentDirector`) acting as orchestrators.
* **Components:** Raw memory structs (`PlanetParamsData`, `AgentDataSphere`) with `[StructLayout]` optimization.
* **Systems:** Stateless workers (`PlanetBaker`, `AgentSystem`) that process data pipelines.

### 2. The "Offline" vs "Online" Pipeline
To ensure zero runtime stuttering, the simulation strictly separates phases:
* **Factory Phase (WorldBuilder):** A stateless builder injects dependencies and utilizes a "Fire-and-Forget" GPU Baker to generate terabytes of procedural data (HeightMaps, NormalMaps, VectorFields).
* **Active Phase (Simulation):** Once the `Planet` Entity is initialized, it acts as a **Single Source of Truth**, serving Read-Only RIDs (Resource IDs) to agents and renderers.

### 3. GPU Compute & Memory Management
* **Std140 Alignment:** Strict memory alignment between C# Structs and GLSL Uniform Buffers to prevent driver overhead.
* **VRAM-First Design:** Terrain and Agent state live primarily in VRAM (`R32F` textures).
* **O(1) CPU Readback:** Implemented a caching strategy where raw bytes are downloaded once during generation, allowing for high-performance CPU Raycasting that perfectly matches the GPU vertex displacement.

## 🔬 Implementation Deep Dive

### 🌐 World Topology: The Normalized Cube Sphere
To avoid the texture distortion and vertex convergence issues typical of standard UV spheres (singularities at the poles), the simulation utilizes a **Normalized Cube Sphere** topology.
* **Data Structure:** All planar data (Height, Normal, Biomes, Vector Fields) is stored in **Cubemap Arrays** (`TextureCubemapArray`).
* **Benefit:** This ensures uniform pixel density across the planetary surface and seamless sampling via 3D direction vectors in the Compute Shaders, eliminating the need for complex UV wrapping logic at the poles.
* **Procedural Generation:** Terrain is generated via GPU-based Multi-Fractal Noise (Simplex/Ridge variations) computed directly into the cubemap faces.

### 🧠 Sociological Optimization: Group-Prototype Inheritance
Simulating 500,000 agents with individual complex psychological profiles is computationally prohibitive. To solve this, the engine implements a **Flyweight Pattern** for sociological traits.
* **Individual Data:** Each agent only stores a `Group_ID` (integer) and physical state (Position, Velocity).
* **Group Traits:** Personality traits (Aggression, Cooperativeness), DNA, and cultural parameters are stored in global lookup tables per group.
* **Expression:** Agents "express" the traits of their assigned group, plus a small procedural variation (entropy) calculated at runtime in the shader. This reduces VRAM usage by **~90%** per agent compared to storing traits individually.

---

## 🏛️ System Architecture Flow

The data flows through a strict "Knowledge Pipeline" designed to decouple systems:

1.  **Core Configuration:** `SimulationConfig` maps high-level parameters to low-level Structs via the `WorldBuilder` Factory.
2.  **GPU Baking (Offline):** The `PlanetBaker` dispatches compute shaders to generate the terrain. Raw bytes are cached to CPU for physics, while Texture RIDs stay in VRAM.
3.  **Simulation Hub (Online):** The `Planet` acts as the central Hub, holding the "Knowledge Fields" (Height, Normal, Vectors).
4.  **Agent Loop:** The `AgentSystem` reads these fields directly on the GPU to update positions in a Storage Buffer, which is then rendered via `MultiMeshInstance` without CPU overhead.

---

## 📂 Project Structure

The repository is organized to enforce the separation of concerns:

- **Scripts/Components/**: Pure Data (Structs, Resources).
- **Scripts/Systems/**: Logic & Processing (Stateless).
- **Scripts/Entities/**: Scene Nodes (Orchestrators).
- **Scripts/Visuals/**: Rendering Logic & LOD.
- **Scripts/World/**: Factories & Builders.
- **Shaders/Compute/**: .glsl (Logic, Physics, Spawning).
- **Shaders/Visual/**: .gdshader (Vertex Displacement).

---

## 🚀 Roadmap & Research Horizons

### ✅ Core Foundation (Completed)
- [x] **Data-Oriented Terrain Engine:** Procedural planet generation using GPU Compute Shaders.
- [x] **Hybrid Raycasting:** Analytic sphere intersection + HeightMap correction for pixel-perfect CPU-GPU sync.
- [x] **Factory Pattern:** Robust `WorldBuilder` for dependency injection and resource management.
- [x] **Knowledge Fields Base:** Centralized Texture RIDs (Height, Normal, Vector) managed by the Planet Hub.

### 🔄 Active Integration
- [ ] **Massive Agent Optimization:** Tuning the `AgentSystem` pipeline to stabilize **500,000 agents @ 60 FPS**. Current benchmark: ~15 FPS @ 500k.
- [ ] **Architectural Polish:** Finalizing the decoupling of the Agent System to fully adhere to pure ECS standards within the Hybrid framework.

### 🔮 Research Horizons (Future Work)
This project aims to simulate a complete living universe. The following features are currently in research and design phases:

#### 🌌 Macro-Scale Simulation
- **Procedural Planetary Systems:** Generation of multiple habitable worlds with unique biomes and atmospheric conditions.
- **Logical LOD (Level of Detail):** Implementation of statistical approximation and "Time-to-Event" simulation for off-screen planets, allowing the simulation of millions of agents across multiple worlds without keeping them all in active memory.
- **Interplanetary Colonization:** Logic for agents to construct vessels, migrate between graph nodes (planets), and establish new colonies.

#### 🧬 Agent Depth & Evolution
- **Cognitive Knowledge Fields:** Extension of the texture-based system to drive complex behaviors (Resource maps, Danger maps, Cultural influence).
- **Evolutionary Traits (DNA):** Implementation of genetic algorithms where group traits and individual DNA mutate over generations based on environmental pressure.
- **Sociological Graph Theory:** Modeling social structures using dynamic graphs to analyze the formation of communities, trade routes, and conflict networks.

#### 💥 Dynamic Topology & Physics
- **Deformable World:** Runtime mesh deformation driven by stochastic events (Meteor impacts, Warfare, Terraforming).
- **Non-Linear Dynamics:** Integration of differential equations to model chaotic population growth and resource depletion cycles.

---

## 🛠️ Installation & Setup

**Requirements:**
* **Godot 4.3+ (.NET Version)**
* .NET SDK 8.0
* Vulkan-compatible GPU (Forward+ Renderer recommended)

**Steps:**
1.  Clone the repository.
2.  Open the project in Godot.
3.  Build the C# solution.
4.  Run `Main.tscn`. *Note: The first run may take a few seconds to compile compute pipelines.*

---

### 🤝 Contribution
This is a high-complexity research project. Contributions focusing on **Compute Shader optimization**, **Parallel Algorithms**, or **Emergent Behavior Logic** are highly appreciated.

*Authored by Matias Collado*
