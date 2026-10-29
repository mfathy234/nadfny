using Nadfny.Features.GitHealth;

namespace Nadfny.Tests;

public sealed class GitHealthParserTests
{
    [Fact]
    public void ParseCountObjects_ReadsEveryKey()
    {
        const string output = """
            count: 12
            size: 48
            in-pack: 3400
            packs: 2
            size-pack: 1500
            prune-packable: 4
            garbage: 1
            size-garbage: 8
            """;

        var counts = GitHealthParser.ParseCountObjects(output.Replace("\n", "\r\n"));

        Assert.Equal(new ObjectCounts(12, 48, 2, 1500, 4, 1, 8), counts);
    }

    [Fact]
    public void ParseCountObjects_MissingKeysReadAsZero()
    {
        var counts = GitHealthParser.ParseCountObjects("count: 0\nsize: 0\n");

        Assert.Equal(new ObjectCounts(0, 0, 0, 0, 0, 0, 0), counts);
        Assert.Equal(new ObjectCounts(0, 0, 0, 0, 0, 0, 0), GitHealthParser.ParseCountObjects(""));
    }

    [Fact]
    public void ParseBranchTracks_ReadsTrackAndUpstream()
    {
        const string output = "main\t\torigin/main\nfeature/a\t[gone]\torigin/feature/a\r\nahead\t[ahead 1]\torigin/ahead\nlocal-only\t\t\n";

        var branches = GitHealthParser.ParseBranchTracks(output);

        Assert.Equal(4, branches.Count);
        Assert.Equal(new BranchTrack("feature/a", "[gone]", "origin/feature/a"), branches[1]);
        Assert.Equal("[ahead 1]", branches[2].Track);
        Assert.Null(branches[3].Upstream);
        Assert.Empty(GitHealthParser.ParseBranchTracks(""));
    }

    [Fact]
    public void GoneBranches_OnlyExactGoneTracks()
    {
        var branches = GitHealthParser.ParseBranchTracks(
            "main\t\torigin/main\n" +
            "feature/a\t[gone]\torigin/feature/a\n" +
            "ahead\t[ahead 1]\torigin/ahead\n" +
            "both\t[ahead 1, behind 2]\torigin/both\n" +
            "zeta\t[gone]\torigin/zeta\n");

        var gone = GitHealthParser.GoneBranches(branches, []);

        Assert.Equal(["feature/a", "zeta"], gone);
    }

    [Fact]
    public void GoneBranches_NeverReturnsExcludedBranches()
    {
        var branches = GitHealthParser.ParseBranchTracks("feature/a\t[gone]\torigin/feature/a\nfeature/b\t[gone]\torigin/feature/b\n");

        Assert.Equal(["feature/b"], GitHealthParser.GoneBranches(branches, ["feature/a"]));
        Assert.Empty(GitHealthParser.GoneBranches(branches, ["feature/a", "feature/b"]));
        Assert.Empty(GitHealthParser.GoneBranches(GitHealthParser.ParseBranchTracks(""), []));
    }

    [Fact]
    public void CheckedOutBranches_IncludesMainAndLinkedWorktreesButNotDetached()
    {
        const string porcelain = """
            worktree C:/Work/Platforms
            HEAD 18c6393f44147bfcf8f7ae0cecd994bd7adad1db
            branch refs/heads/main

            worktree C:/Work/Platforms-trial
            HEAD 211c26cb9e4b4831b46c1dfac9d5761f08e5f192
            branch refs/heads/feature/trial

            worktree C:/Work/Platforms-detached
            HEAD e40b5b65345f018e50a332168c0d910ae0a9fcc6
            detached

            """;

        var checkedOut = GitHealthParser.CheckedOutBranches(porcelain);

        Assert.Equal(2, checkedOut.Count);
        Assert.Contains("main", checkedOut);
        Assert.Contains("feature/trial", checkedOut);
        Assert.Empty(GitHealthParser.CheckedOutBranches(""));
    }

    [Fact]
    public void ParseStashDates_CountsAndFindsOldest()
    {
        var (count, oldest) = GitHealthParser.ParseStashDates("1700000000\n1600000000\r\n1650000000\n");

        Assert.Equal(3, count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1600000000).UtcDateTime, oldest);
    }

    [Fact]
    public void ParseStashDates_NoStashesHasNoDate()
    {
        var (count, oldest) = GitHealthParser.ParseStashDates("");

        Assert.Equal(0, count);
        Assert.Null(oldest);
    }
}
