using System.IO;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.App.ViewModels;

/// <summary>Eine Laufwerkskarte auf dem Startbildschirm.</summary>
public sealed class DriveCard
{
    public required string Root { get; init; }
    public required string Title { get; init; }
    public required string Letter { get; init; }
    public required long TotalBytes { get; init; }
    public required long FreeBytes { get; init; }
    public required string Glyph { get; init; }
    public TimeSpan Delay { get; init; }

    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedShare => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;

    public string PercentText => (UsedShare * 100).ToString("F0", Loc.Culture) + Loc.T("unit.percentSuffix");
    public string UsedText => Loc.F("drive.usedOf", SizeFormat.Bytes(UsedBytes), SizeFormat.Bytes(TotalBytes));
    public string FreeText => Loc.F("drive.free", SizeFormat.Bytes(FreeBytes));

    /// <summary>Ab 90 % wird es eng - der Ring faerbt sich dann warm.</summary>
    public bool IsCritical => UsedShare >= 0.9;
    public bool IsTight => UsedShare >= 0.75 && !IsCritical;

    public static List<DriveCard> ReadAll()
    {
        var cards = new List<DriveCard>();
        int position = 0;
        foreach (DriveInfo drive in DriveInfo.GetDrives().OrderBy(d => d.Name))
        {
            try
            {
                if (!drive.IsReady || drive.TotalSize <= 0) continue;
                string kind = drive.DriveType switch
                {
                    DriveType.Removable => Loc.T("drive.removable"),
                    DriveType.Network => Loc.T("drive.network"),
                    _ => Loc.T("drive.local")
                };
                cards.Add(new DriveCard
                {
                    Root = drive.RootDirectory.FullName,
                    Title = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? kind : drive.VolumeLabel,
                    Letter = drive.Name.TrimEnd('\\'),
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                    Glyph = drive.DriveType switch
                    {
                        DriveType.Removable => "",
                        DriveType.Network => "",
                        _ => ""
                    },
                    Delay = TimeSpan.FromMilliseconds(120 * position++)
                });
            }
            catch (IOException)
            {
            }
        }
        return cards;
    }
}
