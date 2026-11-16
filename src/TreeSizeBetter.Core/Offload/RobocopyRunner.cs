using System.Diagnostics;
using System.Text;

namespace TreeSizeBetter.Core.Offload;

public sealed record CopyResult(int ExitCode, string Output)
{
    /// <summary>Robocopy: 0-7 sind Erfolg (Bits fuer kopiert/extra/abweichend), ab 8 ist etwas schiefgegangen.</summary>
    public bool Success => ExitCode is >= 0 and < 8;
}

/// <summary>
/// Kopiert per Robocopy. Bewusst nicht selbst geschrieben: Robocopy uebernimmt
/// Zeitstempel und Attribute, kopiert parallel, wiederholt bei Sperren und ist
/// seit Jahrzehnten gegen genau die Sonderfaelle gehaertet, die ein eigener
/// Kopierer erst muehsam lernen muesste.
/// </summary>
public static class RobocopyRunner
{
    // Robocopy schreibt in der OEM-Codepage der Konsole (auf deutschem Windows 850).
    // .NET kennt die Codepages erst nach dieser Registrierung.
    static RobocopyRunner() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task<CopyResult> CopyAsync(string source, string target, CancellationToken ct)
    {
        var info = new ProcessStartInfo("robocopy.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.GetEncoding(850)
        };

        info.ArgumentList.Add(source);
        info.ArgumentList.Add(target);
        foreach (string flag in new[]
                 {
                     "/E",            // alle Unterordner, auch leere
                     "/COPY:DAT",     // Daten, Attribute, Zeitstempel. Rechte (S) und Besitzer (O) bewusst
                                      // nicht: das braucht Admin, und das Ziel erbt die Rechte seines Ordners
                     "/DCOPY:DAT",    // Ordner ebenso, inklusive Zeitstempel
                     "/SJ", "/SL",    // Junctions und Symlinks als Verknuepfung kopieren, nie hindurch
                     "/R:1", "/W:1",  // gesperrte Datei: einmal nach 1 s erneut, dann aufgeben statt minutenlang haengen
                     "/MT:16",        // parallel
                     "/NP", "/NFL", "/NDL", "/NJH", "/NJS"   // knapp halten - Fehlerzeilen bleiben trotzdem
                 })
            info.ArgumentList.Add(flag);

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lock (output) output.AppendLine(e.Data.Trim()); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lock (output) output.AppendLine(e.Data.Trim()); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        string text;
        lock (output) text = output.ToString();
        return new CopyResult(process.ExitCode, text);
    }
}
