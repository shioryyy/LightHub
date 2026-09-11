namespace LightHub.Application;

public sealed record MacroEvent(string Kind, int Usage, int DelayMs);
public sealed record LocalMacro(int SchemaVersion, string Id, string Name, string Execution, MacroEvent[] Events)
{
    public LocalMacro Copy() => this with { Events = (MacroEvent[])Events.Clone() };
}
public sealed record MacroIssue(string Code, int? Step = null);

public static class MacroValidator
{
    public const int MaxEvents = 256;
    public static bool IsKeyboardUsage(int usage) => usage is >= 4 and <= 0x73 or >= 0xe0 and <= 0xe7;

    // Drafts may be semantically incomplete, but never have an unbounded or unknown format.
    public static void ValidateShape(LocalMacro macro)
    {
        if (macro is null || macro.SchemaVersion != 1 || !Guid.TryParseExact(macro.Id, "N", out _) ||
            macro.Name is null || macro.Name.Length > 120 || macro.Execution != "single" ||
            macro.Events is null || macro.Events.Length > MaxEvents ||
            macro.Events.Any(e => e is null || e.Kind is not ("down" or "up" or "delay") ||
                (e.Kind == "delay" ? e.Usage != 0 || e.DelayMs is < 1 or > 10000 : !IsKeyboardUsage(e.Usage) || e.DelayMs != 0)))
            throw new MacroLibraryException("Format");
    }

    public static IReadOnlyList<MacroIssue> Check(LocalMacro macro)
    {
        ValidateShape(macro);
        var issues = new List<MacroIssue>();
        if (string.IsNullOrWhiteSpace(macro.Name)) issues.Add(new("NameRequired"));
        if (macro.Events.Length == 0) issues.Add(new("Empty"));
        var pressed = new HashSet<int>();
        int delay = 0;
        for (int i = 0; i < macro.Events.Length; i++)
        {
            var e = macro.Events[i];
            if (e.Kind == "delay") { delay += e.DelayMs; continue; }
            if (e.Kind == "down" && !pressed.Add(e.Usage)) issues.Add(new("DuplicateDown", i + 1));
            if (e.Kind == "up" && !pressed.Remove(e.Usage)) issues.Add(new("UnmatchedUp", i + 1));
            if (pressed.Count(k => k < 0xe0) > 6) issues.Add(new("HeldLimit", i + 1));
        }
        if (delay > 30000) issues.Add(new("Duration"));
        if (pressed.Count != 0) issues.Add(new("HeldAtEnd"));
        return issues;
    }

    public static void Validate(LocalMacro macro)
    {
        var issues = Check(macro);
        if (issues.Count > 0) throw new MacroLibraryException(issues[0].Code);
    }
}

public sealed class MacroLibraryException(string code, Exception? inner = null) : IOException("Macro: " + code, inner)
{
    public string Code { get; } = code;
}

// Capabilities describe implemented backends, never just the presence of a device feature.
public sealed record MacroExecutionCapability(string Target, bool Available, string Reason, int? CapacityBytes);
public static class MacroCapabilities
{
    public static IReadOnlyList<MacroExecutionCapability> Current { get; } = Array.AsReadOnly(new[]
    {
        new MacroExecutionCapability("onboard", false, "OnboardUnavailable", null),
        new MacroExecutionCapability("software", false, "RunnerUnavailable", null)
    });
}
