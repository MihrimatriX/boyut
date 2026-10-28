using Boyut.Core.Ipc;
using Boyut.Core.Scanning;
using Boyut.Core.Search;

namespace Boyut.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void Helper_arguments_round_trip()
    {
        var options = new ScanOptions { IncludeHidden = false, FollowReparsePoints = true, IncludeAlternateStreams = true };
        var args = HelperProtocol.BuildArguments(@"C:\Program Files\", @"C:\Temp\a b.boyut", "boyut-1", options);
        var parsed = HelperProtocol.TryParse(args);
        Assert.NotNull(parsed);
        Assert.Equal(@"C:\Program Files\", parsed.Root);
        Assert.Equal(@"C:\Temp\a b.boyut", parsed.OutputFile);
        Assert.Equal(options.ToBits(), parsed.Options.ToBits());
    }

    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\Program Files", "\"C:\\Program Files\"")]
    [InlineData(@"C:\Program Files\", "\"C:\\Program Files\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    public void Command_line_quoting_follows_windows_rules(string arg, string expected) =>
        Assert.Equal(expected, HelperProtocol.JoinForCommandLine([arg]));

    [Fact]
    public void Progress_lines_round_trip()
    {
        var line = HelperProtocol.Progress(new ScanProgress(1234, 5678, @"C:\a|b", 0.5, "MFT okunuyor"));
        var (kind, p, _) = HelperProtocol.Parse(line);
        Assert.Equal(HelperProtocol.MessageKind.Progress, kind);
        Assert.Equal(1234, p.Items);
        Assert.Equal(5678, p.Bytes);
        Assert.Equal(0.5, p.Fraction);
        Assert.Equal("MFT okunuyor", p.Phase);
        Assert.Equal(@"C:\a¦b", p.CurrentPath);
        Assert.Equal(HelperProtocol.MessageKind.Done, HelperProtocol.Parse(HelperProtocol.Done).Kind);
        Assert.Equal("boom", HelperProtocol.Parse(HelperProtocol.Error("boom")).Error);
    }

    [Theory]
    [InlineData(@"**\Cache\*.log", "*.log")]
    [InlineData(@"?:\pagefile.sys", "pagefile.sys")]
    [InlineData(@"node_modules\**", null)]
    [InlineData(@"Videos\", null)]
    [InlineData("*.mp4", "*.mp4")]
    public void Last_segment_prefilter(string glob, string? expected) => Assert.Equal(expected, Glob.LastSegment(glob));

    [Theory]
    [InlineData(@"**\node_modules", @"C:\a\b\node_modules", true)]
    [InlineData(@"**\node_modules", @"C:\a\foonode_modules", false)]
    [InlineData(@"?:\Windows\WinSxS", @"C:\Windows\WinSxS", true)]
    [InlineData(@"?:\Windows\WinSxS", @"C:\Users\Windows\WinSxS", false)]
    [InlineData(@"Videos\*.mp4", @"D:\Ali\Videos\a.mp4", true)]
    [InlineData(@"Videos\*.mp4", @"D:\Ali\Videos\x\a.mp4", false)]
    [InlineData(@"node_modules\**\*.js", @"C:\p\node_modules\a\b\c.js", true)]
    public void Path_globs(string glob, string path, bool expected)
    {
        var m = Glob.PathMatcher.Create(glob, caseSensitive: false);
        var name = Path.GetFileName(path.Replace('\\', Path.DirectorySeparatorChar));
        bool actual = m.NameMayMatch(name) && m.Path.IsMatch(path);
        Assert.Equal(expected, actual);
    }
}
