using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace HookScan;

public class TargetResolver
{
    private readonly AssemblyDefinition _hooksAsm;
    private readonly AssemblyDefinition _tmlAsm;
    private readonly Dictionary<string, TypeDefinition> _tmlTypes = new();
    private readonly Dictionary<(string HookType, string Event), HookTarget> _resolvedTargets = new();

    public TargetResolver(AssemblyDefinition hooksAsm, AssemblyDefinition tmlAsm)
    {
        _hooksAsm = hooksAsm;
        _tmlAsm = tmlAsm;

        void IndexType(TypeDefinition td)
        {
            _tmlTypes[td.FullName] = td;
            _tmlTypes[td.FullName.Replace('/', '+')] = td;
            _tmlTypes[td.FullName.Replace('+', '/')] = td;
            foreach (var nt in td.NestedTypes) IndexType(nt);
        }

        foreach (var td in _tmlAsm.MainModule.Types)
        {
            IndexType(td);
        }
    }

    public HookTarget ResolveEvent(string hookTypeFullName, string eventName)
    {
        // Normalize IL_ to On_
        string onTypeFullName = hookTypeFullName;
        int idx = onTypeFullName.IndexOf(".IL_");
        if (idx >= 0)
        {
            onTypeFullName = onTypeFullName.Substring(0, idx) + ".On_" + onTypeFullName.Substring(idx + 4);
        }
        else if (onTypeFullName.StartsWith("IL_"))
        {
            onTypeFullName = "On_" + onTypeFullName.Substring(3);
        }

        var key = (onTypeFullName, eventName);
        if (_resolvedTargets.TryGetValue(key, out var cached))
            return cached;

        var hookType = _hooksAsm.MainModule.GetType(onTypeFullName) 
            ?? throw new InvalidOperationException($"Hook type not found in TerrariaHooks: {onTypeFullName}");

        var ev = hookType.Events.FirstOrDefault(e => e.Name == eventName)
            ?? throw new InvalidOperationException($"Event not found in {onTypeFullName}: {eventName}");

        var addM = ev.AddMethod 
            ?? throw new InvalidOperationException($"add_ method not found for event {onTypeFullName}.{eventName}");

        // ldtoken is the first instruction emitted by HookGen for every event
        var ldtoken = addM.Body?.Instructions?.FirstOrDefault(i => i.OpCode == OpCodes.Ldtoken)?.Operand as MethodReference
            ?? throw new InvalidOperationException($"ldtoken MethodReference not found in {addM.FullName}");

        // Find declaring type in tML
        string targetTypeName = ldtoken.DeclaringType.FullName;
        if (!_tmlTypes.TryGetValue(targetTypeName, out var targetTypeDef))
        {
            targetTypeName = targetTypeName.Replace('/', '+');
            if (!_tmlTypes.TryGetValue(targetTypeName, out targetTypeDef))
            {
                targetTypeName = targetTypeName.Replace('+', '/');
                _tmlTypes.TryGetValue(targetTypeName, out targetTypeDef);
            }
        }

        if (targetTypeDef == null)
            throw new InvalidOperationException($"Target declaring type not found in tModLoader: {ldtoken.DeclaringType.FullName}");

        // Match method in targetTypeDef
        var match = targetTypeDef.Methods.FirstOrDefault(m => m.FullName == ldtoken.FullName);
        if (match == null)
        {
            match = targetTypeDef.Methods.FirstOrDefault(m =>
                m.Name == ldtoken.Name &&
                m.Parameters.Count == ldtoken.Parameters.Count &&
                m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(ldtoken.Parameters.Select(p => p.ParameterType.FullName)) &&
                m.ReturnType.FullName == ldtoken.ReturnType.FullName);
        }

        if (match == null)
        {
            // Fallback: match by orig delegate Invoke signature
            var orig = hookType.NestedTypes.FirstOrDefault(nt => nt.Name == "orig_" + eventName);
            if (orig != null)
            {
                var invoke = orig.Methods.FirstOrDefault(m => m.Name == "Invoke");
                if (invoke != null)
                {
                    match = MatchMethodByInvokeSignature(targetTypeDef, eventName, invoke);
                }
            }
        }

        if (match == null)
            throw new InvalidOperationException($"Could not resolve target method in tModLoader for {onTypeFullName}.{eventName}");

        var target = new HookTarget
        {
            Assembly = "tModLoader",
            Type = match.DeclaringType.FullName.Replace('/', '+'),
            Method = match.IsConstructor ? "ctor" : match.Name,
            FullName = match.FullName,
            IsStatic = match.IsStatic,
            IsCtor = match.IsConstructor
        };

        _resolvedTargets[key] = target;
        return target;
    }

    private MethodDefinition? MatchMethodByInvokeSignature(TypeDefinition targetType, string eventName, MethodDefinition invoke)
    {
        bool isInstance = invoke.Parameters.Count > 0 && invoke.Parameters[0].Name == "self";
        var expectedParams = isInstance ? invoke.Parameters.Skip(1).ToList() : invoke.Parameters.ToList();
        var expectedRet = invoke.ReturnType;
        bool isCtor = eventName == "ctor" || eventName.StartsWith("ctor_");
        bool isCCtor = eventName == "cctor" || eventName.StartsWith("cctor_");

        var candidates = new List<MethodDefinition>();
        foreach (var m in targetType.Methods)
        {
            if (isCtor)
            {
                if (!m.IsConstructor || m.IsStatic) continue;
            }
            else if (isCCtor)
            {
                if (!m.IsConstructor || !m.IsStatic) continue;
            }
            else
            {
                if (m.IsConstructor) continue;
                if (m.Name != eventName && !eventName.StartsWith(m.Name + "_")) continue;
            }

            if (m.IsStatic == isInstance) continue;
            if (!isCtor && !isCCtor && m.ReturnType.FullName != expectedRet.FullName && expectedRet.FullName != "System.ValueType") continue;
            if (m.Parameters.Count != expectedParams.Count) continue;

            bool paramsMatch = true;
            for (int i = 0; i < m.Parameters.Count; i++)
            {
                string p1 = m.Parameters[i].ParameterType.FullName.TrimEnd('&');
                string p2 = expectedParams[i].ParameterType.FullName.TrimEnd('&');
                if (p1 != p2 && p2 != "System.ValueType" && p2 != "System.Object")
                {
                    paramsMatch = false;
                    break;
                }
            }
            if (paramsMatch) candidates.Add(m);
        }

        if (candidates.Count > 1)
        {
            var exact = candidates.Where(c => c.Name == eventName).ToList();
            if (exact.Count == 1) return exact[0];
            var longest = candidates.OrderByDescending(c => c.Name.Length).ToList();
            if (longest[0].Name.Length > longest[1].Name.Length) return longest[0];
            throw new InvalidOperationException($"Ambiguous target methods for event {eventName}: {string.Join(", ", candidates.Select(c => c.FullName))}");
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    public HookTarget? ResolveDirectTarget(string typeFullName, string methodName, MethodReference? hintDelegate = null)
    {
        string normalized = typeFullName.Replace('/', '+');
        if (!_tmlTypes.TryGetValue(normalized, out var td))
        {
            normalized = typeFullName.Replace('+', '/');
            _tmlTypes.TryGetValue(normalized, out td);
        }

        if (td == null) return null;

        var methods = td.Methods.Where(m => m.Name == methodName || (methodName == "ctor" && m.IsConstructor && !m.IsStatic)).ToList();
        if (methods.Count == 1)
        {
            var m = methods[0];
            return CreateTargetFromMethodDefinition(m);
        }

        if (methods.Count > 1 && hintDelegate != null)
        {
            var hintDef = hintDelegate.Resolve() ?? hintDelegate;
            var hintParams = hintDef.Parameters.ToList();

            // In hook delegates, param 0 is usually orig delegate, param 1 may be self
            // Try matching remaining parameters to candidate methods
            var candidates = new List<MethodDefinition>();
            foreach (var m in methods)
            {
                int mParamCount = m.Parameters.Count;
                // Check if hintParams end with mParamCount parameters
                if (hintParams.Count >= mParamCount)
                {
                    var slice = hintParams.Skip(hintParams.Count - mParamCount).ToList();
                    bool match = true;
                    for (int i = 0; i < mParamCount; i++)
                    {
                        string p1 = m.Parameters[i].ParameterType.FullName.TrimEnd('&');
                        string p2 = slice[i].ParameterType.FullName.TrimEnd('&');
                        if (p1 != p2)
                        {
                            match = false;
                            break;
                        }
                    }
                    if (match) candidates.Add(m);
                }
            }

            if (candidates.Count == 1)
            {
                return CreateTargetFromMethodDefinition(candidates[0]);
            }
        }

        if (methods.Count > 1)
        {
            throw new InvalidOperationException($"Ambiguous target methods in {typeFullName} for method {methodName}: {methods.Count} overloads found");
        }

        return null;
    }

    public HookTarget CreateTargetFromMethodDefinition(MethodDefinition method)
    {
        return new HookTarget
        {
            Assembly = method.Module.Assembly.Name.Name,
            Type = method.DeclaringType.FullName.Replace('/', '+'),
            Method = method.IsConstructor ? "ctor" : method.Name,
            FullName = method.FullName,
            IsStatic = method.IsStatic,
            IsCtor = method.IsConstructor
        };
    }
}
