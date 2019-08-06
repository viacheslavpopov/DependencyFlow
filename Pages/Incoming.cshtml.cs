using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Octokit;

namespace DependencyFlow.Pages
{
    public class IncomingModel : PageModel
    {
        private static readonly Regex _repoParser = new Regex(@"https?://(www\.)?github.com/(?<owner>[A-Za-z0-9-_\.]+)/(?<repo>[A-Za-z0-9-_\.]+)");
        private readonly swaggerClient _client;
        private readonly GitHubClient _github;

        public IncomingModel(swaggerClient client, GitHubClient github)
        {
            _client = client;
            _github = github;
        }

        public IReadOnlyList<IncomingRepo> IncomingRepositories { get; private set; }
        public RateLimit CurrentRateLimit { get; private set; }

        public async Task OnGet(int channelId, string repo)
        {
            // The 'repo' is URL encoded (because it's a URL within a URL). Decode it before calling the API.
            var decodedRepo = WebUtility.UrlDecode(repo);
            var latest = await _client.GetLatestAsync(decodedRepo, null, null, channelId, null, null, false, ApiVersion10._20190116);
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

                incoming.Add(new IncomingRepo()
                {
                    Build = build,
                    ShortName = gitHubInfo?.Repo,
                    CommitUrl = GetCommitUrl(build),
                    BuildUrl = $"https://dev.azure.com/{build.AzureDevOpsAccount}/{build.AzureDevOpsProject}/_build/results?buildId={build.AzureDevOpsBuildId}&view=results",
                    CommitDistance = gitHubInfo == null ? null : await ComputeCommitsBehindAsync(gitHubInfo, build)
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
            return $"{build.AzureDevOpsRepository}/commits?itemPath=%2F&itemVersion=GC{build.Commit}";;
        }

        private async Task<int?> ComputeCommitsBehindAsync(GitHubInfo gitHubInfo, Build build)
        {
            var comparison = await _github.Repository.Commit.Compare(gitHubInfo.Owner, gitHubInfo.Repo, build.Commit, build.GitHubBranch);

            // We're using the branch as the "head" so "ahead by" is actually how far the branch (i.e. "master") is
            // ahead of the commit. So it's also how far **behind** the commit is from the branch head.
            return comparison.AheadBy;
        }
    }

    public class IncomingRepo
    {
        public Build Build { get; set; }
        public string ShortName { get; set; }
        public int? CommitDistance { get; set; }
        public string CommitUrl { get; set; }
        public string BuildUrl { get; set; }
    }

    public class GitHubInfo
    {
        public string Owner { get; set; }
        public string Repo { get; set; }
    }
}
