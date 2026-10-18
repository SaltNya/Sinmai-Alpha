// Headless math shim; the geometry files are linked from production.
using System;
namespace UnityEngine;
public struct Vector3(float x, float y, float z = 0)
{
    public float x = x, y = y, z = z;
    public static Vector3 zero => new();
    public static Vector3 right => new(1,0);
    public static Vector3 forward => new(0,0,1);
    public float sqrMagnitude => x*x+y*y+z*z;
    public float magnitude => MathF.Sqrt(sqrMagnitude);
    public Vector3 normalized => magnitude > .00001f ? this * (1f / magnitude) : zero;
    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x+b.x,a.y+b.y,a.z+b.z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x-b.x,a.y-b.y,a.z-b.z);
    public static Vector3 operator *(Vector3 a, float b) => new(a.x*b,a.y*b,a.z*b);
    public static Vector3 operator *(float a, Vector3 b) => b*a;
    public static float Distance(Vector3 a, Vector3 b) => (a-b).magnitude;
    public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;
    public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a+(b-a)*Mathf.Clamp01(t);
}
public struct Vector4(float x, float y, float z, float w) { public float x=x,y=y,z=z,w=w; }
public struct Vector2(float x,float y)
{
    public float x=x,y=y;
    public static Vector2 zero=>new();
public float sqrMagnitude=>x*x+y*y;
public Vector2 normalized=>magnitude>.00001f?this/magnitude:zero;
public static Vector2 operator /(Vector2 a,float b)=>new(a.x/b,a.y/b);
public static Vector2 operator *(Vector2 a,float b)=>new(a.x*b,a.y*b);
    public static implicit operator Vector2(Vector3 v)=>new(v.x,v.y);
    public static implicit operator Vector3(Vector2 v)=>new(v.x,v.y);
    public float magnitude=>MathF.Sqrt(x*x+y*y);
    public static Vector2 operator -(Vector2 a,Vector2 b)=>new(a.x-b.x,a.y-b.y);
    public static Vector2 operator +(Vector2 a,Vector2 b)=>new(a.x+b.x,a.y+b.y);
    public static Vector2 Lerp(Vector2 a,Vector2 b,float t)=>new(Mathf.Lerp(a.x,b.x,t),Mathf.Lerp(a.y,b.y,t));
    public static float Distance(Vector2 a,Vector2 b)=>MathF.Sqrt((a.x-b.x)*(a.x-b.x)+(a.y-b.y)*(a.y-b.y));
    public static float SignedAngle(Vector2 a,Vector2 b)=>MathF.Atan2(a.x*b.y-a.y*b.x,a.x*b.x+a.y*b.y)*180/MathF.PI;
}
public static class Mathf
{
    public const float PI = MathF.PI;
    public const float Deg2Rad=PI/180,Rad2Deg=180/PI;
    public static float Sqrt(float x)=>MathF.Sqrt(x);
    public static float Acos(float x)=>MathF.Acos(x);
    public static float Sign(float x)=>x>=0?1:-1;
    public static float Round(float x)=>MathF.Round(x);
    public static int RoundToInt(float x)=>(int)MathF.Round(x);
    public static int CeilToInt(float x)=>(int)MathF.Ceiling(x);
    public static float Clamp(float x,float a,float b)=>System.Math.Clamp(x,a,b);
    public static int Clamp(int x,int a,int b)=>System.Math.Clamp(x,a,b);
    public static int Min(int x,int y)=>System.Math.Min(x,y);
    public static float DeltaAngle(float a,float b) { var d=Repeat(b-a,360);return d>180?d-360:d; }
    public static float LerpAngle(float a,float b,float t)=>a+DeltaAngle(a,b)*Clamp01(t);
    public static float Abs(float v)=>MathF.Abs(v);
    public static float Min(float a,float b)=>MathF.Min(a,b);
    public static float Max(float a,float b)=>MathF.Max(a,b);
    public static float Atan2(float y,float x)=>MathF.Atan2(y,x);
    public static float Cos(float x)=>MathF.Cos(x);
    public static float Sin(float x)=>MathF.Sin(x);
    public static float Clamp01(float x)=>System.Math.Clamp(x,0,1);
    public static float Repeat(float t,float length)=>System.Math.Clamp(t-MathF.Floor(t/length)*length,0,length);
    public static float Lerp(float a,float b,float t)=>a+(b-a)*Clamp01(t);
    public static float SmoothStep(float a,float b,float t) { t=Clamp01(t); t=-2*t*t*t+3*t*t; return b*t+a*(1-t); }
}
