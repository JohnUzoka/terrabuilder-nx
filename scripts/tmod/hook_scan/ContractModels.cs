using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HookScan;

public class HookInventory
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("game")]
    public GameAssemblies Game { get; set; } = new();

    [JsonPropertyName("mods")]
    public List<ModEntry> Mods { get; set; } = new();

    [JsonPropertyName("on_hooks")]
    public List<OnHookEntry> OnHooks { get; set; } = new();

    [JsonPropertyName("il_hooks")]
    public List<ILHookEntry> ILHooks { get; set; } = new();

    [JsonPropertyName("runtime_detours")]
    public List<RuntimeDetourEntry> RuntimeDetours { get; set; } = new();

    [JsonPropertyName("unsupported")]
    public List<UnsupportedEntry> Unsupported { get; set; } = new();
}

public class GameAssemblies
{
    [JsonPropertyName("tModLoader")]
    public AssemblyInfo TModLoader { get; set; } = new();

    [JsonPropertyName("TerrariaHooks")]
    public AssemblyInfo TerrariaHooks { get; set; } = new();
}

public class AssemblyInfo
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("mvid")]
    public string Mvid { get; set; } = "";
}

public class ModEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("dll")]
    public string Dll { get; set; } = "";

    [JsonPropertyName("dll_sha256")]
    public string DllSha256 { get; set; } = "";

    [JsonPropertyName("mvid")]
    public string Mvid { get; set; } = "";

    [JsonPropertyName("mod_references")]
    public List<string> ModReferences { get; set; } = new();

    [JsonPropertyName("dll_references")]
    public List<string> DllReferences { get; set; } = new();
}

public class HookTarget
{
    [JsonPropertyName("assembly")]
    public string Assembly { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = "";

    [JsonPropertyName("is_static")]
    public bool IsStatic { get; set; }

    [JsonPropertyName("is_ctor")]
    public bool IsCtor { get; set; }
}

public class HookRegistration
{
    [JsonPropertyName("mod")]
    public string Mod { get; set; } = "";

    [JsonPropertyName("caller")]
    public string Caller { get; set; } = "";

    [JsonPropertyName("il_offset")]
    public int IlOffset { get; set; }

    [JsonPropertyName("op")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Op { get; set; }
}

public class OnHookEntry
{
    [JsonPropertyName("hook_type")]
    public string HookType { get; set; } = "";

    [JsonPropertyName("event")]
    public string Event { get; set; } = "";

    [JsonPropertyName("target")]
    public HookTarget Target { get; set; } = new();

    [JsonPropertyName("registrations")]
    public List<HookRegistration> Registrations { get; set; } = new();
}

public class ILHookEntry
{
    [JsonPropertyName("mechanism")]
    public string Mechanism { get; set; } = "";

    [JsonPropertyName("target")]
    public HookTarget? Target { get; set; }

    [JsonPropertyName("manipulator")]
    public string? Manipulator { get; set; }

    [JsonPropertyName("uses_emit_delegate")]
    public bool UsesEmitDelegate { get; set; }

    [JsonPropertyName("delegate_slots")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DelegateSlotEntry>? DelegateSlots { get; set; }

    [JsonPropertyName("shape_hash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShapeHash { get; set; }

    [JsonPropertyName("is_pure")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsPure { get; set; }

    [JsonPropertyName("registrations")]
    public List<HookRegistration> Registrations { get; set; } = new();
}

public class DelegateSlotEntry
{
    [JsonPropertyName("slot_id")]
    public int SlotId { get; set; }

    [JsonPropertyName("slot_name")]
    public string SlotName { get; set; } = "";

    [JsonPropertyName("delegate_type")]
    public string DelegateType { get; set; } = "";
}

public class RuntimeDetourEntry
{
    [JsonPropertyName("mechanism")]
    public string Mechanism { get; set; } = "";

    [JsonPropertyName("target")]
    public HookTarget? Target { get; set; }

    [JsonPropertyName("target_resolution")]
    public string TargetResolution { get; set; } = "";

    [JsonPropertyName("registrations")]
    public List<HookRegistration> Registrations { get; set; } = new();
}

public class UnsupportedEntry
{
    [JsonPropertyName("mod")]
    public string Mod { get; set; } = "";

    [JsonPropertyName("caller")]
    public string Caller { get; set; } = "";

    [JsonPropertyName("il_offset")]
    public int IlOffset { get; set; }

    [JsonPropertyName("reason_code")]
    public string ReasonCode { get; set; } = "";

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";
}
