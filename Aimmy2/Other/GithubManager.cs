using Newtonsoft.Json;
using System.Net.Http;
using System.Text.Json;

namespace Whales2.Other
{
    public class GithubManager : IDisposable
    {
        private readonly HttpClient httpClient;

        public GithubManager()
        {
            httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Whales2");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        }

        private class GitHubContent
        {
            public string name { get; set; }
        }

        public async Task<(string tagName, string downloadUrl)> GetLatestReleaseInfo(string owner, string repo)
        {
            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";

            using var response = await httpClient.GetAsync(apiUrl);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(content);

            string tagName = data["tag_name"].ToString() ?? throw new InvalidOperationException("Tag name is missing in the response");
            string downloadUrl = ((JsonElement)data["assets"]).EnumerateArray().First().GetProperty("browser_download_url").ToString();

            return (tagName, downloadUrl);
        }

        /// <summary>
        /// The latest release's tag and its first .zip asset (falls back to the first asset).
        /// </summary>
        public async Task<(string tagName, string downloadUrl, long sizeBytes)> GetLatestReleaseZip(string owner, string repo)
        {
            using var response = await httpClient.GetAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest");
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            string tagName = root.GetProperty("tag_name").GetString() ?? throw new InvalidOperationException("The release has no tag");

            JsonElement? chosen = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                chosen ??= asset;
                if (asset.GetProperty("name").GetString()?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                {
                    chosen = asset;
                    break;
                }
            }
            if (chosen is not JsonElement zip)
                throw new InvalidOperationException($"Release {tagName} has no download attached");

            return (tagName, zip.GetProperty("browser_download_url").GetString()!, zip.GetProperty("size").GetInt64());
        }

        public async Task<IEnumerable<string?>> FetchGithubFilesAsync(string url)
        {
            var response = await httpClient.GetAsync(url);

            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();

            List<GitHubContent>? contents = JsonConvert.DeserializeObject<List<GitHubContent>>(content);
            if (contents == null)
            {
                throw new InvalidOperationException("Failed to deserialize GitHub content or Github content is empty.");
            }

            return contents.Select(c => c.name);
        }

        public void Dispose()
        {
            httpClient.Dispose();
        }
    }
}