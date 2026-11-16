using System.Xml.Linq;

namespace Any2Hevc;

/// <summary>Talks to Plex Media Server so it picks up replaced files without waiting for its next scan.</summary>
static class Plex
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    static async Task<string> GetAsync(string url, string token, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url.TrimEnd('/') + path);
        req.Headers.Add("X-Plex-Token", token);
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>Names of the server's libraries; used to test the connection.</summary>
    public static async Task<List<string>> LibrariesAsync(string url, string token)
    {
        var xml = XDocument.Parse(await GetAsync(url, token, "/library/sections"));
        return xml.Descendants("Directory").Select(d => (string?)d.Attribute("title") ?? "?").ToList();
    }

    public static Task RefreshAllAsync(string url, string token) =>
        GetAsync(url, token, "/library/sections/all/refresh");
}
