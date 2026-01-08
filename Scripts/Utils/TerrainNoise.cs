using Godot;
using System;

public static class TerrainNoise
{
	// --- HELPERS PARA SIMULAR GLSL ---
	private static Vector4 Vec4(float v) => new Vector4(v, v, v, v);
	private static Vector3 Vec3(float v) => new Vector3(v, v, v);

	private static Vector4 Mod289(Vector4 x)
	{
		return x - Floor(x * (1.0f / 289.0f)) * 289.0f;
	}

	private static float Mod289(float x)
	{
		return x - Mathf.Floor(x * (1.0f / 289.0f)) * 289.0f;
	}

	private static Vector4 Permute(Vector4 x)
	{
		return Mod289(((x * 34.0f) + Vec4(1.0f)) * x);
	}

	private static Vector4 TaylorInvSqrt(Vector4 r)
	{
		return Vec4(1.79284291400159f) - 0.85373472095314f * r;
	}

	private static Vector4 Floor(Vector4 v) 
	{ 
		return new Vector4(Mathf.Floor(v.X), Mathf.Floor(v.Y), Mathf.Floor(v.Z), Mathf.Floor(v.W)); 
	}

	private static Vector3 Floor(Vector3 v)
	{
		return new Vector3(Mathf.Floor(v.X), Mathf.Floor(v.Y), Mathf.Floor(v.Z));
	}
	
	// Sobrecarga para Vector4
	private static Vector4 Step(Vector4 edge, Vector4 x)
	{
		return new Vector4(
			x.X >= edge.X ? 1.0f : 0.0f,
			x.Y >= edge.Y ? 1.0f : 0.0f,
			x.Z >= edge.Z ? 1.0f : 0.0f,
			x.W >= edge.W ? 1.0f : 0.0f
		);
	}

	// Sobrecarga para Vector3 (CRÍTICA para arreglar el error de línea 82)
	private static Vector3 Step(Vector3 edge, Vector3 x)
	{
		return new Vector3(
			x.X >= edge.X ? 1.0f : 0.0f,
			x.Y >= edge.Y ? 1.0f : 0.0f,
			x.Z >= edge.Z ? 1.0f : 0.0f
		);
	}
	
	private static Vector3 Min(Vector3 a, Vector3 b)
	{
		return new Vector3(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y), Mathf.Min(a.Z, b.Z));
	}
	
	private static Vector3 Max(Vector3 a, Vector3 b)
	{
		return new Vector3(Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y), Mathf.Max(a.Z, b.Z));
	}

	private static Vector4 Max(Vector4 a, float b)
	{
		 return new Vector4(Mathf.Max(a.X, b), Mathf.Max(a.Y, b), Mathf.Max(a.Z, b), Mathf.Max(a.W, b));
	}

	// --- SNOISE IMPLEMENTATION ---
	private static float Snoise(Vector3 v)
	{
		Vector2 C = new Vector2(1.0f / 6.0f, 1.0f / 3.0f);
		Vector4 D = new Vector4(0.0f, 0.5f, 1.0f, 2.0f);

		// First corner
		float dot1 = v.Dot(Vec3(C.Y));
		Vector3 i  = Floor(v + Vec3(dot1));
		
		float dot2 = i.Dot(Vec3(C.X));
		Vector3 x0 = v - i + Vec3(dot2);

		// Other corners
		// Aquí se usa la sobrecarga Step(Vector3, Vector3)
		Vector3 g = Step(new Vector3(x0.Y, x0.Z, x0.X), x0);
		
		Vector3 l = Vector3.One - g;
		Vector3 i1 = Min(g, new Vector3(l.Z, l.X, l.Y));
		Vector3 i2 = Max(g, new Vector3(l.Z, l.X, l.Y));

		// x1, x2, x3 offsets
		Vector3 x1 = x0 - i1 + Vec3(C.X);
		Vector3 x2 = x0 - i2 + Vec3(C.Y); 
		Vector3 x3 = x0 - Vec3(D.Y);

		// Permutations
		i = new Vector3(Mod289(i.X), Mod289(i.Y), Mod289(i.Z));
		
		Vector4 p = Permute(Permute(Permute(
			 Vec4(i.Z) + new Vector4(0.0f, i1.Z, i2.Z, 1.0f))
		   + Vec4(i.Y) + new Vector4(0.0f, i1.Y, i2.Y, 1.0f))
		   + Vec4(i.X) + new Vector4(0.0f, i1.X, i2.X, 1.0f));

		float n_ = 0.142857142857f; 
		Vector3 ns = n_ * new Vector3(D.W, D.Y, D.Z) - new Vector3(D.X, D.Z, D.X);

		Vector4 j = p - 49.0f * Floor(p * ns.Z * ns.Z); 
		
		Vector4 x_ = Floor(j * ns.Z);
		Vector4 y_ = Floor(j - 7.0f * x_); 

		Vector4 x = x_ * ns.X + Vec4(ns.Y);
		Vector4 y = y_ * ns.X + Vec4(ns.Y);
		
		// CORRECCIÓN LÍNEA 111: Usar Vec4(1.0f) para poder restar vectores
		Vector4 h = Vec4(1.0f) - x.Abs() - y.Abs();

		Vector4 b0 = new Vector4(x.X, x.Y, y.X, y.Y);
		Vector4 b1 = new Vector4(x.Z, x.W, y.Z, y.W);

		Vector4 s0 = Floor(b0) * 2.0f + Vec4(1.0f);
		Vector4 s1 = Floor(b1) * 2.0f + Vec4(1.0f);
		
		Vector4 sh = -Step(h, Vector4.Zero);

		Vector4 a0 = new Vector4(b0.X, b0.Z, b0.Y, b0.W) + new Vector4(s0.X, s0.Z, s0.Y, s0.W) * new Vector4(sh.X, sh.X, sh.Y, sh.Y);
		Vector4 a1 = new Vector4(b1.X, b1.Z, b1.Y, b1.W) + new Vector4(s1.X, s1.Z, s1.Y, s1.W) * new Vector4(sh.Z, sh.Z, sh.W, sh.W);

		Vector3 p0 = new Vector3(a0.X, a0.Y, h.X);
		Vector3 p1 = new Vector3(a0.Z, a0.W, h.Y);
		Vector3 p2 = new Vector3(a1.X, a1.Y, h.Z);
		Vector3 p3 = new Vector3(a1.Z, a1.W, h.W);

		Vector4 norm = TaylorInvSqrt(new Vector4(p0.Dot(p0), p1.Dot(p1), p2.Dot(p2), p3.Dot(p3)));
		p0 *= norm.X;
		p1 *= norm.Y;
		p2 *= norm.Z;
		p3 *= norm.W;

		Vector4 m = Max(Vec4(0.6f) - new Vector4(x0.Dot(x0), x1.Dot(x1), x2.Dot(x2), x3.Dot(x3)), 0.0f);
		m = m * m;
		
		return 42.0f * (
			(m.X * m.X) * p0.Dot(x0) + 
			(m.Y * m.Y) * p1.Dot(x1) + 
			(m.Z * m.Z) * p2.Dot(x2) + 
			(m.W * m.W) * p3.Dot(x3)
		);
	}

	// --- FRACTALES ---

	private static float FbmSoft(Vector3 x, int octaves)
	{
		float v = 0.0f; float a = 0.5f; Vector3 shift = Vec3(100.0f);
		float freq = 1.0f;
		for (int i = 0; i < octaves; ++i) {
			v += a * Snoise(x * freq);
			x += shift; a *= 0.5f; freq *= 2.0f;
		}
		return v;
	}

	private static float RidgedNoise(Vector3 x, int octaves)
	{
		float v = 0.0f; float a = 1.0f; float freq = 1.0f; float prev_n = 1.0f;
		for (int i = 0; i < octaves; ++i) {
			float n = Snoise(x * freq);
			n = 1.0f - Mathf.Abs(n);
			n *= n;
			v += n * a * prev_n;
			prev_n = n; a *= 0.5f; freq *= 2.0f;
		}
		return v;
	}

	private static Vector3 Warp(Vector3 p)
	{
		float q = FbmSoft(p * 0.5f, 2);
		float displacement = q * 0.3f;
		return p + Vec3(displacement);
	}

	// --- API PÚBLICA ---
	public static float GetTerrainAAA(Vector3 dir, PlanetParamsData p)
	{
		Vector3 scaledP = dir.Normalized() * p.NoiseScale;
		Vector3 warpedP = Warp(scaledP);

		float continental = FbmSoft(warpedP * 0.5f, 3);
		float mountains = RidgedNoise(warpedP * 1.5f, 4);
		float mountainMask = Mathf.SmoothStep(0.0f, 0.4f, continental);

		float h = continental + (mountains * mountainMask * 0.6f);
		return p.Radius + (h * p.NoiseHeight);
	}
}
