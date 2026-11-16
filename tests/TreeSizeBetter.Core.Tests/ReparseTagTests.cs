using TreeSizeBetter.Core.Scanning;

namespace TreeSizeBetter.Core.Tests;

public class ReparseTagTests
{
    [Theory]
    [InlineData(0x9000001Au)]   // CLOUD
    [InlineData(0x9000101Au)]   // CLOUD_1
    [InlineData(0x9000301Au)]   // CLOUD_3, OneDrive
    [InlineData(0x9000701Au)]   // CLOUD_7, iCloud
    [InlineData(0x9000F01Au)]   // CLOUD_F
    public void RecognisesEveryCloudProvider(uint tag)
        => Assert.True(ReparseTags.IsCloudPlaceholder(tag));

    [Theory]
    [InlineData(ReparseTags.MountPoint)]
    [InlineData(ReparseTags.SymLink)]
    [InlineData(ReparseTags.AppExecLink)]
    [InlineData(0x9001301Au)]   // falsches Bit ausserhalb des Provider-Halbbytes
    public void DoesNotMistakeLinksForCloudFolders(uint tag)
        => Assert.False(ReparseTags.IsCloudPlaceholder(tag));
}
