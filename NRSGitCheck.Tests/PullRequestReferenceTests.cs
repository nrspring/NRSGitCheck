using NRSGitCheck.Models;
using Xunit;

namespace NRSGitCheck.Tests;

/// <summary>
/// Parsing of whatever the user pastes into the "review a pull request" box, and
/// the origin-URL check that keeps a link from another project out of this repo.
/// </summary>
public sealed class PullRequestReferenceTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo/pull/123")]
    [InlineData("https://github.com/owner/repo/pull/123/")]
    [InlineData("https://github.com/owner/repo/pull/123/files")]
    [InlineData("https://github.com/owner/repo/pull/123/commits/abc123")]
    [InlineData("https://github.com/owner/repo/pull/123?w=1")]
    [InlineData("https://github.com/owner/repo/pull/123#discussion_r456")]
    [InlineData("http://github.com/owner/repo/pull/123")]
    [InlineData("github.com/owner/repo/pull/123")]
    [InlineData("  https://github.com/owner/repo/pull/123  ")]
    public void Parses_a_link_in_its_many_shapes(string input)
    {
        Assert.True(PullRequestReference.TryParse(input, out var pr, out var error));
        Assert.Null(error);
        Assert.Equal(123, pr!.Number);
        Assert.Equal("owner/repo", pr.Slug);
        Assert.Equal("pr-123", pr.LocalBranch);
        Assert.Equal("refs/pull/123/head", pr.RemoteRef);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("#123")]
    [InlineData("  123 ")]
    public void Parses_a_bare_number(string input)
    {
        Assert.True(PullRequestReference.TryParse(input, out var pr, out _));
        Assert.Equal(123, pr!.Number);
        Assert.Null(pr.Slug);   // nothing to cross-check against origin
    }

    [Fact]
    public void Handles_an_enterprise_host_and_a_dot_git_suffix()
    {
        Assert.True(PullRequestReference.TryParse(
            "https://git.example.com/team/tool.git/pull/7", out var pr, out _));
        Assert.Equal(7, pr!.Number);
        Assert.Equal("team/tool", pr.Slug);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not a link")]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/issues/12")]
    [InlineData("https://github.com/owner/repo/pull/abc")]
    [InlineData("0")]
    [InlineData("-4")]
    public void Rejects_what_is_not_a_pull_request(string? input)
    {
        Assert.False(PullRequestReference.TryParse(input, out var pr, out var error));
        Assert.Null(pr);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("https://github.com/nrspring/NRSGitCheck.git", "nrspring/NRSGitCheck")]
    [InlineData("https://github.com/nrspring/NRSGitCheck", "nrspring/NRSGitCheck")]
    [InlineData("git@github.com:nrspring/NRSGitCheck.git", "nrspring/NRSGitCheck")]
    [InlineData("ssh://git@github.com/nrspring/NRSGitCheck.git", "nrspring/NRSGitCheck")]
    [InlineData("https://token@github.com/nrspring/NRSGitCheck.git", "nrspring/NRSGitCheck")]
    public void Reads_the_slug_out_of_a_remote_url(string url, string expected)
    {
        Assert.Equal(expected, PullRequestReference.SlugFromRemoteUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    public void Returns_no_slug_for_an_unparseable_remote(string? url)
    {
        Assert.Null(PullRequestReference.SlugFromRemoteUrl(url));
    }

    [Fact]
    public void A_link_from_another_project_is_detectable()
    {
        Assert.True(PullRequestReference.TryParse(
            "https://github.com/someone-else/other/pull/9", out var pr, out _));

        var origin = PullRequestReference.SlugFromRemoteUrl("git@github.com:nrspring/NRSGitCheck.git");
        Assert.NotEqual(pr!.Slug, origin);
    }
}
