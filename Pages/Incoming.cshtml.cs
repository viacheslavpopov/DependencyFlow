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

        public IReadOnlyList<IncomingRepo> IncomingRepositories { get; private set; }
        public RateLimit CurrentRateLimit { get; private set; }

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

                GitHubInfo gitHubInfo = null;
                if (!string.IsNullOrEmpty(build.GitHubRepository))
                {
                    var match = _repoParser.Match(build.GitHubRepository);
                    if (match.Success)
                    {
                        gitHubInfo = new GitHubInfo()
                        {
                            Owner = match.Groups["owner"].Value,
                            Repo = match.Groups["repo"].Value
                        };
                    }
                }

                var (commitDistance, commitAge) = await GetCommitInfo(gitHubInfo, build);

                incoming.Add(new IncomingRepo()
                {
                    Build = build,
                    ShortName = gitHubInfo?.Repo,
                    CommitUrl = GetCommitUrl(build),
                    BuildUrl = $"https://dev.azure.com/{build.AzureDevOpsAccount}/{build.AzureDevOpsProject}/_build/results?buildId={build.AzureDevOpsBuildId}&view=results",
                    CommitDistance = commitDistance,
                    CommitAge = commitAge
                });
            }
            IncomingRepositories = incoming;

            CurrentRateLimit = _github.GetLastApiInfo().RateLimit;
        }

        private string GetCommitUrl(Build build)
        {
            if (!string.IsNullOrEmpty(build.GitHubRepository))
            {
                return $"{build.GitHubRepository}/commits/{build.Commit}";
            }
            return $"{build.AzureDevOpsRepository}/commits?itemPath=%2F&itemVersion=GC{build.Commit}";
        }

        private async Task<CompareResult> GetCommitsBehindAsync(GitHubInfo gitHubInfo, Build build)
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

        private async Task<(int?, DateTimeOffset)> GetCommitInfo(GitHubInfo gitHubInfo, Build build)
        {
            var commitAge = build.DateProduced;
            int? commitDistance = null;
            if (gitHubInfo != null)
            {
                var comparison = await GetCommitsBehindAsync(gitHubInfo, build);
                if (comparison != null)
                {
                    foreach (var commit in comparison.Commits)
                    {
                        if (commit.Commit.Committer.Date < commitAge)
                        {
                            commitAge = commit.Commit.Committer.Date;
                        }
                    }

                    // We're using the branch as the "head" so "ahead by" is actually how far the branch (i.e. "master") is
                    // ahead of the commit. So it's also how far **behind** the commit is from the branch head.
                    commitDistance = comparison.AheadBy;
                }
            }
            return (commitDistance, commitAge);
        }
    }

    public class IncomingRepo
    {
        public Build Build { get; set; }
        public string ShortName { get; set; }
        public int? CommitDistance { get; set; }
        public string CommitUrl { get; set; }
        public string BuildUrl { get; set; }
        public DateTimeOffset CommitAge { get; set; }
    }

    public class GitHubInfo
    {
        public string Owner { get; set; }
        public string Repo { get; set; }
    }
}
