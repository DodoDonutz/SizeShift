namespace TreeSizeBetter.Core.Scanning;

/// <summary>
/// Reparse-Tags, die auf einem normalen Windows-PC tatsaechlich auftauchen.
/// Relevant, weil die App Links klar benennen und Cloud-Platzhalter nicht als
/// riesige lokale Dateien fehldeuten soll.
/// </summary>
public static class ReparseTags
{
    public const uint MountPoint = 0xA0000003;   // Junction - das, was die App beim Umlagern setzt
    public const uint SymLink = 0xA000000C;
    public const uint AppExecLink = 0x8000001B;  // Store-App-Starter in WindowsApps
    public const uint OneDrive = 0x9000001A;     // IO_REPARSE_TAG_CLOUD
    public const uint Cloud = 0x9000001A;
    public const uint Cloud1 = 0x9000101A;
    public const uint Cloud2 = 0x9000201A;
    public const uint Cloud3 = 0x9000301A;
    public const uint Cloud4 = 0x9000401A;
    public const uint Cloud5 = 0x9000501A;
    public const uint Cloud6 = 0x9000601A;
    public const uint Cloud7 = 0x9000701A;
    public const uint Cloud8 = 0x9000801A;
    public const uint Cloud9 = 0x9000901A;
    public const uint CloudA = 0x9000A01A;
    public const uint CloudB = 0x9000B01A;
    public const uint CloudC = 0x9000C01A;
    public const uint CloudD = 0x9000D01A;
    public const uint CloudE = 0x9000E01A;
    public const uint CloudF = 0x9000F01A;
    public const uint Dedup = 0x80000013;
    public const uint Wcifs = 0x80000018;        // Windows-Container, u. a. Store-Apps
    public const uint WimBoot = 0x80000008;
    public const uint Nfs = 0x80000014;
    public const uint Hsm = 0xC0000004;
    public const uint Hsm2 = 0x80000006;

    /// <summary>
    /// Cloud-Platzhalter (OneDrive & Co.). Die Tags unterscheiden sich nur im
    /// Nibble fuer den Provider, deshalb wird maskiert statt einzeln verglichen.
    /// </summary>
    public static bool IsCloudPlaceholder(uint tag)
        => (tag & 0xFFFF0FFF) == 0x9000001A;   // Provider-Nummer steckt in Bits 12-15: 0x9000_X_01A

    public static bool IsDirectoryLink(uint tag)
        => tag is MountPoint or SymLink;

    public static string Describe(uint tag) => tag switch
    {
        0 => string.Empty,
        MountPoint => "Junction",
        SymLink => "Symlink",
        AppExecLink => Loc.T("reparse.appExec"),
        Dedup => Loc.T("reparse.dedup"),
        Wcifs => Loc.T("reparse.container"),
        WimBoot => Loc.T("reparse.wim"),
        Nfs => "NFS",
        Hsm or Hsm2 => Loc.T("reparse.archive"),
        _ when IsCloudPlaceholder(tag) => Loc.T("reparse.cloud"),
        _ => $"Reparse 0x{tag:X8}"
    };
}
