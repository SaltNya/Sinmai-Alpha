using System;
using System.Linq;
using Mono.Cecil;

static class GameContract
{
    // Read the actual game binary; no Unity initialization or detouring of the
    // real game occurs here. The fixture suite executes the Harmony wrappers.
    internal static void Verify(string path)
    {
        using var module=ModuleDefinition.ReadModule(path);
        var root=module.GetType("Monitor.SlideRoot")??throw new Exception("Missing SlideRoot");
        foreach(var field in new[]{("_hitIndex","System.Int32"),("_hitSubIndex","System.Int32"),("_hitIn","System.Boolean"),
            ("_hitAreaList","System.Collections.Generic.List`1<Manager.SlideManager/HitArea>")})
            if(root.Fields.SingleOrDefault(f=>f.Name==field.Item1)?.FieldType.FullName!=field.Item2)
                throw new Exception("Game field signature changed: "+field.Item1);
        Method(root,"Initialize","System.Void",("note","Manager.NoteData"));
        Method(root,"NextCheck","System.Boolean");
        Method(root,"NoteCheck","System.Void");
        Method(root,"CheckSlideTouch","System.Boolean",("index","System.Int32"),("In","System.Boolean"));
        Method(module.GetType("Process.GameProcess"),"OnRelease","System.Void");
        var initialize=root.Methods.Single(m=>m.Name=="Initialize");
        if(!initialize.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="GetSlideHitArea"))
            throw new Exception("Initialize no longer obtains slide hit tables");
        Console.WriteLine("PASS actual game DLL: patch method names, parameter names and private field types verified (metadata only).");
    }
    static void Method(TypeDefinition type,string name,string returns,params (string Name,string Type)[] parameters)
    {
        var method=type.Methods.Single(m=>m.Name==name&&m.Parameters.Count==parameters.Length);
        if(method.ReturnType.FullName!=returns||method.Parameters.Where((p,i)=>p.Name!=parameters[i].Name||p.ParameterType.FullName!=parameters[i].Type).Any())
            throw new Exception("Game method signature changed: "+type.FullName+"."+name);
    }
}
