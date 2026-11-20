namespace WPM.App.Diagnostics;

public enum DiagnosticCategory { System, Network, Devices, Graphics, Storage, Power }

public sealed record DiagnosticItem(string Label, string Value);

public sealed record DiagnosticSection(
    string Title,
    IReadOnlyList<DiagnosticItem> Items,
    string? Issue = null)
{
    public DiagnosticCategory Category { get; init; }
}

public sealed record DiagnosticReport(
    DateTimeOffset CollectedAt,
    IReadOnlyList<DiagnosticSection> Sections);
