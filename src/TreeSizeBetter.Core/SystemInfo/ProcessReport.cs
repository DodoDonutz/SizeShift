using System.Text;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.SystemInfo;

/// <summary>
/// Prozess-Aufzeichnung als Markdown fuer eine KI: welche Programme liefen, wer sie
/// gestartet hat, und wie viel Rechenleistung, Arbeitsspeicher und Festplatte sie
/// gebraucht haben. Reine Auflistung - die Bewertung macht die KI.
/// </summary>
public static class ProcessReport
{
    private const double MinCpu = 0.05;
    private const long MinRam = 50L * 1024 * 1024;
    private const long MinIo = 50L * 1024 * 1024;

    public static bool IsNotable(ProcessGroupStats g) =>
        g.CpuAvgPercent >= MinCpu || g.RamAvgBytes >= MinRam || g.ReadBytes + g.WriteBytes >= MinIo;

    public static string Build(ProcessRecording r, SystemSnapshot? system)
    {
        var c = Loc.Culture;
        var md = new StringBuilder();
        md.AppendLine("# " + Loc.F("procreport.title", system?.ComputerName ?? Environment.MachineName));
        md.AppendLine();
        md.AppendLine(Loc.F("procreport.created", r.StartedLocal.ToString("g", c), FormatDuration(r.Duration), r.Samples));
        md.AppendLine();
        md.AppendLine("> " + Loc.T("procreport.forAi"));
        if (!r.Elevated) md.AppendLine(">\n> " + Loc.T("procreport.notElevated"));
        md.AppendLine();

        md.AppendLine("## " + Loc.T("procreport.section.summary"));
        md.AppendLine();
        if (system?.Cpu is { } cpu) md.AppendLine($"- {Loc.T("system.card.cpu")}: {cpu.Name} ({cpu.Cores}/{cpu.Threads})");
        if (system?.Memory is { } mem) md.AppendLine($"- {Loc.T("system.card.memory")}: {SizeFormat.Bytes(mem.TotalBytes)}");
        md.AppendLine("- " + Loc.F("procreport.systemCpu", r.SystemCpuAvgPercent.ToString("0.0", c), r.SystemCpuPeakPercent.ToString("0.0", c)));
        md.AppendLine("- " + Loc.F("procreport.programs", r.Groups.Count, r.Groups.Sum(g => g.Instances)));
        md.AppendLine();
        md.AppendLine(Loc.T("procreport.units"));
        if (r.Groups.Any(g => !string.IsNullOrEmpty(g.Services))) md.AppendLine(Loc.T("procreport.services"));
        md.AppendLine();

        List<ProcessGroupStats> notable = r.Groups.Where(IsNotable).ToList();
        md.AppendLine("## " + Loc.T("procreport.section.programs"));
        md.AppendLine();
        md.AppendLine($"| {Loc.T("procreport.col.program")} | {Loc.T("procreport.col.cpuAvg")} | {Loc.T("procreport.col.cpuPeak")} | " +
                      $"{Loc.T("procreport.col.ramAvg")} | {Loc.T("procreport.col.ramPeak")} | {Loc.T("procreport.col.disk")} | " +
                      $"{Loc.T("procreport.col.instances")} | {Loc.T("procreport.col.parent")} | {Loc.T("procreport.col.description")} | {Loc.T("procreport.col.path")} |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|---|---|");
        foreach (ProcessGroupStats g in notable)
        {
            md.AppendLine($"| {Esc(g.DisplayName)} | {g.CpuAvgPercent.ToString("0.00", c)} % | {g.CpuPeakPercent.ToString("0.0", c)} % | " +
                          $"{SizeFormat.Bytes(g.RamAvgBytes)} | {SizeFormat.Bytes(g.RamPeakBytes)} | " +
                          $"{SizeFormat.Bytes(g.ReadBytes)} / {SizeFormat.Bytes(g.WriteBytes)} | {g.Instances} | {Esc(g.Parent)} | " +
                          $"{Esc(Join(g.Description, g.Publisher))} | `{Esc(g.Path ?? "-")}` |");
        }
        md.AppendLine();

        List<ProcessGroupStats> quiet = r.Groups.Where(g => !IsNotable(g)).ToList();
        if (quiet.Count > 0)
        {
            md.AppendLine("## " + Loc.F("procreport.section.quiet", quiet.Count));
            md.AppendLine();
            md.AppendLine(string.Join(", ", quiet.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase).Select(g => Esc(g.DisplayName))));
            md.AppendLine();
        }

        md.AppendLine("---");
        md.AppendLine(Loc.T("procreport.footer"));
        return md.ToString();
    }

    public static string FormatDuration(TimeSpan d) =>
        d.TotalMinutes >= 1 ? Loc.F("procreport.minutes", Math.Round(d.TotalMinutes, 1)) : Loc.F("procreport.seconds", (int)d.TotalSeconds);

    private static string Join(string a, string b) =>
        string.Join(" · ", new[] { a, b }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string Esc(string text) => text.Replace("|", "\\|").Replace("\n", " ").Replace("\r", string.Empty);
}
