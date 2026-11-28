using Gizlet.Core.Updates;

namespace Gizlet.Core.Tests;

public class InstallerTrustTests
{
    [Theory]
    [InlineData(true, true, false, InstallerVerdict.Install)]
    [InlineData(true, false, false, InstallerVerdict.Install)]
    [InlineData(false, true, true, InstallerVerdict.WrongPublisher)]
    [InlineData(false, true, false, InstallerVerdict.WrongPublisher)]
    [InlineData(null, true, true, InstallerVerdict.Install)]
    [InlineData(null, true, false, InstallerVerdict.WrongPublisher)]
    [InlineData(null, false, false, InstallerVerdict.DevelopmentBuild)]
    [InlineData(null, false, true, InstallerVerdict.DevelopmentBuild)]
    public void Judges(bool? samePublisher, bool packaged, bool allowUnsigned, InstallerVerdict expected) =>
        Assert.Equal(expected, InstallerTrust.Judge(samePublisher, packaged, allowUnsigned));
}
