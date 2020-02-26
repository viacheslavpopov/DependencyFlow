using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Octokit;

namespace DependencyFlow.Pages
{
    public class IncomingModel : PageModel
    {
        private static readonly Regex _repoParser = new Regex(@"https?://(www\.)?github.com/(?<owner>[A-Za-z0-9-_\.]+)/(?<repo>[A-Za-z0-9-_\.]+)");
        private readonly swaggerClient _client;
        private readonly GitHubClient _github;
        private readonly ILogger<IncomingModel> _logger;

        public IncomingModel(swaggerClient client, GitHubClient github, ILogger<IncomingModel> logger)
        {
            _client = client;
            _github = github;
            _logger = logger;
        }

        public IReadOnlyList<IncomingRepo>? IncomingRepositories { get; private set; }
        public RateLimit? CurrentRateLimit { get; private set; }

        public async Task OnGet(int channelId, string owner, string repo)
        {
            var repoUrl = $"https://github.com/{owner}/{repo}";
            var latest = await _client.GetLatestAsync(repoUrl, null, null, channelId, null, null, false, ApiVersion10._20190116);
            var graph = await _client.GetBuildGraphAsync(latest.Id, (ApiVersion9)ApiVersion40._20190116);
            latest = graph.Builds[latest.Id.ToString()];

            var incoming = new List<IncomingRepo>();
            foreach (var dep in latest.Dependencies)
            {
                var build = graph.Builds[dep.BuildId.ToString()];

                GitHubInfo? gitHubInfo = null;
                if (!string.IsNullOrEmpty(build.GitHubRepository))
                {
                    var match = _repoParser.Match(build.GitHubRepository);
                    if (match.Success)
                    {
                        gitHubInfo = new GitHubInfo(
                            match.Groups["owner"].Value,
                            match.Groups["repo"].Value);
                    }
                }

                if (!IncludeRepo(gitHubInfo))
                {
                    continue;
                }

                var (commitDistance, commitAge) = await GetCommitInfo(gitHubInfo, build);

                incoming.Add(new IncomingRepo(
                    build,
                    shortName: gitHubInfo?.Repo,
                    commitDistance,
                    GetCommitUrl(build),
                    buildUrl: $"https://dev.azure.com/{build.AzureDevOpsAccount}/{build.AzureDevOpsProject}/_build/results?buildId={build.AzureDevOpsBuildId}&view=results",
                    commitAge
                ));
            }
            IncomingRepositories = incoming;

            CurrentRateLimit = _github.GetLastApiInfo().RateLimit;
        }

        private bool IncludeRepo(GitHubInfo? gitHubInfo)
        {
            if (string.Equals(gitHubInfo?.Owner, "dotnet", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(gitHubInfo?.Repo, "blazor", StringComparison.OrdinalIgnoreCase))
            {
                // We don't want to track dependency staleness of the Blazor repo
                // because it's not part of our process of automated dependency PRs.
                return false;
            }

            return true;
        }

        private string GetCommitUrl(Build build)
        {
            if (!string.IsNullOrEmpty(build.GitHubRepository))
            {
                return $"{build.GitHubRepository}/commits/{build.Commit}";
            }
            return $"{build.AzureDevOpsRepository}/commits?itemPath=%2F&itemVersion=GC{build.Commit}";
        }

        private async Task<CompareResult?> GetCommitsBehindAsync(GitHubInfo gitHubInfo, Build build)
        {
            try
            {
                var comparison = await _github.Repository.Commit.Compare(gitHubInfo.Owner, gitHubInfo.Repo, build.Commit, build.GitHubBranch);

                return comparison;
            }
            catch (NotFoundException)
            {
                _logger.LogWarning("Failed to compare commit history for '{0}/{1}' between '{2}' and '{3}'.", gitHubInfo.Owner, gitHubInfo.Repo,
                    build.Commit, build.GitHubBranch);
                return null;
            }
        }

        private async Task<(int?, DateTimeOffset?)> GetCommitInfo(GitHubInfo? gitHubInfo, Build build)
        {
            DateTimeOffset? commitAge = build.DateProduced;
            int? commitDistance = null;
            if (gitHubInfo != null)
            {
                var comparison = await GetCommitsBehindAsync(gitHubInfo, build);

                // We're using the branch as the "head" so "ahead by" is actually how far the branch (i.e. "master") is
                // ahead of the commit. So it's also how far **behind** the commit is from the branch head.
                commitDistance = comparison?.AheadBy;

                if (comparison != null && comparison.Commits.Count > 0)
                {
                    // Follow the first parent starting at the last unconsumed commit until we find the commit directly after our current consumed commit
                    var nextCommit = comparison.Commits[comparison.Commits.Count - 1];
                    while (nextCommit.Parents[0].Sha != build.Commit)
                    {
                        bool foundCommit = false;
                        foreach (var commit in comparison.Commits)
                        {
                            if (commit.Sha == nextCommit.Parents[0].Sha)
                            {
                                nextCommit = commit;
                                foundCommit = true;
                                break;
                            }
                        }

                        if (foundCommit == false)
                        {
                            // something went wrong
                            _logger.LogWarning("Failed to follow commit parents and find correct commit age.");
                            commitAge = null;
                            return (commitDistance, commitAge);
                        }
                    }

                    commitAge = nextCommit.Commit.Committer.Date;
                }
            }
            return (commitDistance, commitAge);
        }
    }

    public class IncomingRepo
    {
        public Build Build { get; }
        public string? ShortName { get; }
        public int? CommitDistance { get; }
        public string CommitUrl { get; }
        public string BuildUrl { get; }
        public DateTimeOffset? CommitAge { get; }

        public IncomingRepo(Build build, string? shortName, int? commitDistance, string commitUrl, string buildUrl, DateTimeOffset? commitAge)
        {
            Build = build;
            ShortName = shortName;
            CommitDistance = commitDistance;
            CommitUrl = commitUrl;
            BuildUrl = buildUrl;
            CommitAge = commitAge;
        }
    }

    public class GitHubInfo
    {
        public string Owner { get; }
        public string Repo { get; }

        public GitHubInfo(string owner, string repo)
        {
            Owner = owner;
            Repo = repo;
        }
    }
}
