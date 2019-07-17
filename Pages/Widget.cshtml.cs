using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DependencyFlow.Pages
{
    public class WidgetModel : PageModel
    {
        private readonly swaggerClient _client;

        public WidgetModel(swaggerClient client)
        {
            _client = client;
        }

        public Build LatestBuild { get; private set; }
        public BuildGraph BuildGraph { get; private set; }

        public async Task OnGet(int channelId, string repo)
        {
            // The 'repo' is URL encoded (because it's a URL within a URL). Decode it before calling the API.
            var decodedRepo = WebUtility.UrlDecode(repo);
            var latest = await _client.GetLatestAsync(decodedRepo, null, null, channelId, null, null, false, ApiVersion10._20190116);
            BuildGraph = await _client.GetBuildGraphAsync(latest.Id, (ApiVersion9)ApiVersion40._20190116);
            LatestBuild = BuildGraph.Builds[latest.Id.ToString()];
        }
    }
}
