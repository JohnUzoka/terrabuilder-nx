using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

// Host boundaries only. GetValue itself is copied instruction-for-instruction from the input images.
public sealed class DescriptorProofContext
{
    public readonly List<string> Trace = new();
    public readonly List<string> Lines = new();
    public readonly List<string> EntryLines = new();
    public readonly List<string> Assertions = new();
    public bool Capture = true;
    public string? ThrowAt;
    public int ThrowOccurrence = 1;
    public readonly Dictionary<string, int> Visits = new();
    public readonly Exception BoundaryFailure = new InvalidOperationException("boundary failure");
    public readonly Exception GetterFailure = new InvalidOperationException("getter failure");
    public readonly object Identity = new();
    public int EntryNames, HandlerConstructors, Literals, Formatted, Clears, EntryWrites, OtherWrites, Asserts;
    public int GetterReads, SiteReads, SiteNameReads;
    public string ErrorTemplate = "";
    public bool Extender, Substitute;
    public object? InvocationTarget;
    public object? Value;
    public bool ChangingValue;
    public MethodInfo? Getter = typeof(DescriptorProofComponent).GetProperty(nameof(DescriptorProofComponent.Value))!.GetMethod;
    public Exception? LookupFailure, GetterThrown;
    public bool NullMessage, ThrowMessage;
    public ISite? Site;
    public string?[] SiteNames = new string?[] { "fixture-site" };
    public string PropertyName = "Value";

    public void Visit(string name)
    {
        if (Capture) Trace.Add(name);
        if (ThrowAt != null)
        {
            Visits.TryGetValue(name, out int count); Visits[name] = ++count;
            if (ThrowAt == name && count == ThrowOccurrence) throw BoundaryFailure;
        }
    }
    public void Diagnostic(string name)
    {
        if (ThrowAt == name) throw BoundaryFailure;
    }
}

public sealed class DescriptorProofHost
{
    public readonly DescriptorProofContext Context;
    public Type _componentClass = typeof(DescriptorProofComponent);
    public DescriptorProofHost(DescriptorProofContext context) { Context = context; }
    public string EntryName
    {
        get { Context.EntryNames++; Context.Diagnostic("entry-name"); return Context.PropertyName; }
    }
    public string Name { get { Context.Visit("name"); return Context.PropertyName; } }
    public bool IsExtender { get { Context.Visit("is-extender"); return Context.Extender; } }
    public MethodInfo? GetMethodValue
    {
        get { Context.Visit("get-method"); if (Context.LookupFailure != null) throw Context.LookupFailure; return Context.Getter; }
    }
    public object? GetInvocationTarget(Type type, object component)
    {
        if (type != _componentClass) throw new InvalidOperationException("changed component-class field argument");
        Context.Visit("invocation-target");
        return Context.Substitute ? Context.InvocationTarget : component;
    }
}

public sealed class DescriptorProofComponent : IComponent
{
    public readonly DescriptorProofContext Context;
    public DescriptorProofComponent(DescriptorProofContext context) { Context = context; }
    public object? Value
    {
        get
        {
            Context.Visit("getter"); Context.GetterReads++;
            if (Context.GetterThrown != null) throw Context.GetterThrown;
            return Context.ChangingValue ? Context.GetterReads : Context.Value;
        }
    }
    public ISite? Site { get { Context.Visit("site"); Context.SiteReads++; return Context.Site; } set { Context.Site = value; } }
    public event EventHandler? Disposed { add { } remove { } }
    public void Dispose() { Context.Visit("dispose"); }
}

public sealed class DescriptorProofOrdinary
{
    public readonly DescriptorProofContext Context;
    public DescriptorProofOrdinary(DescriptorProofContext context) { Context = context; }
    public object? Value
    {
        get
        {
            Context.Visit("getter"); Context.GetterReads++;
            if (Context.GetterThrown != null) throw Context.GetterThrown;
            return Context.Value;
        }
    }
}

public sealed class DescriptorProofSite : ISite
{
    readonly DescriptorProofContext context;
    public DescriptorProofSite(DescriptorProofContext context, IComponent component) { this.context = context; Component = component; }
    public IComponent Component { get; }
    public IContainer? Container => null;
    public bool DesignMode => false;
    public string? Name
    {
        get { context.Visit("site-name"); int index = context.SiteNameReads++; return context.SiteNames[Math.Min(index, context.SiteNames.Length - 1)]; }
        set { throw new NotSupportedException(); }
    }
    public object? GetService(Type serviceType) { context.Visit("site-service"); return null; }
}

public sealed class DescriptorProofMessageException : Exception
{
    readonly DescriptorProofContext context;
    public DescriptorProofMessageException(DescriptorProofContext context) : base("unused") { this.context = context; }
    public override string Message
    {
        get { context.Visit("exception-message"); if (context.ThrowMessage) throw context.BoundaryFailure; return context.NullMessage ? null! : "custom getter message"; }
    }
}

// The real host interpolated-string handler performs the work. Counters do not model its behavior.
public ref struct DescriptorProofHandler
{
    DefaultInterpolatedStringHandler handler;
    public DescriptorProofHandler(int literalLength, int formattedCount)
    {
        var c = DescriptorProofDiagnostics.Current; c.HandlerConstructors++; c.Diagnostic("entry-handler");
        handler = new DefaultInterpolatedStringHandler(literalLength, formattedCount);
    }
    public void AppendLiteral(string value)
    {
        var c = DescriptorProofDiagnostics.Current; c.Literals++; c.Diagnostic("entry-literal"); handler.AppendLiteral(value);
    }
    public void AppendFormatted(string? value)
    {
        var c = DescriptorProofDiagnostics.Current; c.Formatted++; c.Diagnostic("entry-formatted"); handler.AppendFormatted(value);
    }
    public string ToStringAndClear()
    {
        var c = DescriptorProofDiagnostics.Current; c.Clears++; c.Diagnostic("entry-clear"); return handler.ToStringAndClear();
    }
}

public static class DescriptorProofDiagnostics
{
    [ThreadStatic] static DescriptorProofContext? current;
    public static DescriptorProofContext Current { get => current ?? throw new InvalidOperationException("proof context not installed"); set => current = value; }
    // These must not have Conditional("DEBUG"): both target calls must always remain executable.
    public static void EntryWriteLine(string value)
    {
        var c = Current; c.EntryWrites++; c.Diagnostic("entry-write"); if (c.Capture) c.EntryLines.Add(value);
    }
    public static void WriteLine(string value)
    {
        var c = Current; c.OtherWrites++; c.Visit("write-line"); if (c.Capture) c.Lines.Add(value);
    }
    public static void Assert(bool condition, string message)
    {
        var c = Current; c.Asserts++; c.Visit("assert"); if (c.Capture) c.Assertions.Add(condition + ":" + message);
    }
    public static string ErrorPropertyAccessorException
    {
        get { Current.Visit("error-resource"); return Current.ErrorTemplate; }
    }
    public static string Format(string format, object? first, object? second, object? third)
    {
        Current.Visit("error-format"); return string.Format(CultureInfo.CurrentCulture, format, first, second, third);
    }
}
