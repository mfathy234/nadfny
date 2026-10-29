using Nadfny.Features.Worktrees;

namespace Nadfny.Tests;

public sealed class WorktreeParserTests
{
    [Fact]
    public void ParsePorcelain_ReadsMainAndLinkedWorktrees()
    {
        const string output = """
            worktree C:/Work/Platforms
            HEAD 18c6393f44147bfcf8f7ae0cecd994bd7adad1db
            branch refs/heads/main

            worktree C:/Work/Platforms-trial
            HEAD 211c26cb9e4b4831b46c1dfac9d5761f08e5f192
            branch refs/heads/feature/trial
            locked

            worktree C:/Work/Platforms-detached
            HEAD e40b5b65345f018e50a332168c0d910ae0a9fcc6
            detached
            prunable gitdir file points to non-existent location

            """;

        var result = WorktreeParser.ParsePorcelain(output);

        Assert.Equal(3, result.Count);
        Assert.Equal(@"C:\Work\Platforms", result[0].Path);
        Assert.Equal("main", result[0].Branch);
        Assert.Equal("feature/trial", result[1].Branch);
        Assert.True(result[1].Locked);
        Assert.True(result[2].Detached);
        Assert.True(result[2].Prunable);
        Assert.Null(result[2].Branch);
    }

    [Fact]
    public void ParsePorcelain_HandlesCrLfAndEmptyInput()
    {
        Assert.Empty(WorktreeParser.ParsePorcelain(""));
        var result = WorktreeParser.ParsePorcelain("worktree C:/a\r\nHEAD abc\r\nbranch refs/heads/x\r\n\r\nworktree C:/b\r\nbare\r\n");
        Assert.Equal(2, result.Count);
        Assert.True(result[1].Bare);
    }

    [Fact]
    public void ParseStatusV2_CountsChangesAndAheadBehind()
    {
        const string output = """
            # branch.oid 18c6393f44147bfcf8f7ae0cecd994bd7adad1db
            # branch.head feature/x
            # branch.upstream origin/feature/x
            # branch.ab +3 -1
            1 .M N... 100644 100644 100644 abc abc src/a.cs
            ? untracked.txt
            """;

        var s = WorktreeParser.ParseStatusV2(output);

        Assert.Equal(2, s.ChangedFiles);
        Assert.Equal(3, s.Ahead);
        Assert.Equal(1, s.Behind);
        Assert.True(s.HasUpstream);
    }

    [Fact]
    public void ParseStatusV2_GoneUpstreamCountsAsNotPushed()
    {
        var s = WorktreeParser.ParseStatusV2("# branch.oid abc\n# branch.head x\n# branch.upstream origin/x\n");
        Assert.False(s.HasUpstream);
    }

    [Fact]
    public void ParseStatusV2_NoUpstreamMeansNothingPushed()
    {
        var s = WorktreeParser.ParseStatusV2("# branch.oid abc\n# branch.head local-only\n");
        Assert.False(s.HasUpstream);
        Assert.Equal(0, s.ChangedFiles);
        Assert.Equal(0, s.Ahead);
    }
}
