using Gizlet.Core.Updates;

namespace Gizlet.Core.Tests;

public class InstallerTrustTests
{
    [Theory]
    [InlineData(true, true, InstallerVerdict.Install)]
    [InlineData(true, false, InstallerVerdict.Install)]
    [InlineData(false, true, InstallerVerdict.WrongPublisher)]
    [InlineData(false, false, InstallerVerdict.WrongPublisher)]
    [InlineData(null, true, InstallerVerdict.Install)]
    [InlineData(null, false, InstallerVerdict.DevelopmentBuild)]
    public void Judges(bool? samePublisher, bool packaged, InstallerVerdict expected) =>
        Assert.Equal(expected, InstallerTrust.Judge(samePublisher, packaged));
}
