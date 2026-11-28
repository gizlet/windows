using Gizlet.Core.Flash;

namespace Gizlet.Core.Tests;

public class HelperTrustTests
{
    const string Built = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    const string Other = "60303ae22b998861bce3b28f33eec1be758a213c86c93c076dbe9f558c11c752";

    [Theory]
    [InlineData(null, Built, Built, true, true)]
    [InlineData(null, Built, Other, true, false)]
    [InlineData(true, Built, Built, true, true)]
    [InlineData(true, Built, Other, true, false)]
    [InlineData(false, Built, Built, true, false)]
    [InlineData(null, null, Other, true, false)]
    [InlineData(null, null, Other, false, true)]
    [InlineData(false, null, Other, false, false)]
    public void Judges(bool? samePublisher, string? expected, string actual, bool packaged, bool allowed) =>
        Assert.Equal(allowed, HelperTrust.Allows(samePublisher, expected, actual, packaged));

    [Fact]
    public void IgnoresHexCase() => Assert.True(HelperTrust.Allows(null, Built.ToUpperInvariant(), Built, true));
}
