namespace Gizlet.Core.Tests;

public class SemVersionTests
{
    [Theory]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("12.0.345", 12, 0, 345)]
    public void Parses(string text, int major, int minor, int patch) =>
        Assert.Equal(new SemVersion(major, minor, patch), SemVersion.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-beta")]
    [InlineData("1.-2.3")]
    [InlineData("1.2.٣")]
    [InlineData("1234567890.0.0")]
    public void Refuses(string text) => Assert.False(SemVersion.TryParse(text, out _));

    [Fact]
    public void ParsesTags()
    {
        Assert.True(SemVersion.TryParseTag("v1.2.3", out var version));
        Assert.Equal(new SemVersion(1, 2, 3), version);
        Assert.False(SemVersion.TryParseTag("1.2.3", out _));
    }

    [Fact]
    public void OrdersNumerically()
    {
        Assert.True(SemVersion.Parse("0.10.0") > SemVersion.Parse("0.9.9"));
        Assert.True(SemVersion.Parse("1.0.0") > SemVersion.Parse("0.99.99"));
        Assert.Equal("0.10.0", SemVersion.Parse("0.10.0").ToString());
    }
}
