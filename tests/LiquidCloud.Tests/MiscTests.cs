namespace LiquidCloud.Tests;

public class MiscTests
{
    [Theory]
    [InlineData("https://api-v2.soundcloud.com/x?client_id=abc123&q=1", "client_id=***")]
    [InlineData("oauth_token=secret.value", "oauth_token=***")]
    [InlineData("Authorization: OAuth 2-12345-abc", "OAuth ***")]
    [InlineData("https://cb?code=ABC&state=1", "code=***")]
    public void Log_masks_secrets(string raw, string expected)
    {
        var masked = Log.Mask(raw);
        Assert.Contains(expected, masked);
        Assert.DoesNotContain("abc123", masked);
        Assert.DoesNotContain("secret.value", masked);
        Assert.DoesNotContain("2-12345-abc", masked);
    }

    [Fact]
    public void Track_key_and_duration_text()
    {
        var t = new Track("42", "T", "A", null, null, "sc", null, 125_000);
        Assert.Equal("sc:42", t.Key);
        Assert.Equal("2:05", t.DurationText);
    }

    [Fact]
    public void Untranslated_text_falls_back_to_english_and_formats()
    {
        Assert.Equal("Hello", Loc.T("Hello"));
        Assert.Equal("3 of 5", Loc.T("{0} of {1}", 3, 5));
    }

    [Fact]
    public void Offline_exception_has_message()
    {
        Assert.False(string.IsNullOrWhiteSpace(new OfflineException().Message));
    }
}

public class PluralTests
{
    [Fact]
    public void Russian_and_english_song_counts()
    {
        Loc.Apply(Loc.Russian);
        Assert.Equal("1 песня", Loc.Songs(1));
        Assert.Equal("2 песни", Loc.Songs(2));
        Assert.Equal("5 песен", Loc.Songs(5));
        Assert.Equal("11 песен", Loc.Songs(11));
        Assert.Equal("21 песня", Loc.Songs(21));
        Assert.Equal("24 песни", Loc.Songs(24));
        Assert.Equal("112 песен", Loc.Songs(112));
        Loc.Apply(Loc.English);
        Assert.Equal("1 song", Loc.Songs(1));
        Assert.Equal("7 songs", Loc.Songs(7));
        Loc.Apply(Loc.Russian);
    }
}
