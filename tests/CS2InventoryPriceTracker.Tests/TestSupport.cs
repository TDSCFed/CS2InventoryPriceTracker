using System.Net;
using System.Text;

// The app's SteamDiagnostics uses MAUI device APIs; the linked files only need Write().
namespace CS2InventoryPriceTracker.Services
{
    public static class SteamDiagnostics
    {
        public static readonly List<string> Lines = new();

        public static void Write(string line)
        {
            lock (Lines)
                Lines.Add(line);
        }
    }
}

namespace CS2InventoryPriceTracker.Tests
{
    using CS2InventoryPriceTracker.Services;

    /// <summary>Answers HTTP requests from a table of URL fragments, so tests never touch the network.</summary>
    public sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly List<(string UrlPart, HttpStatusCode Status, string Body)> _routes = new();

        public List<string> Requests { get; } = new();

        public FakeHttpHandler Add(string urlPart, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes.Add((urlPart, status, body));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var route = _routes.LastOrDefault(r => url.Contains(r.UrlPart, StringComparison.Ordinal));
            if (route.UrlPart is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Body, Encoding.UTF8, "application/json")
            });
        }
    }

    public static class TestData
    {
        /// <summary>The real item table, as bundled in the app.</summary>
        public static ItemCatalog Catalog() =>
            new(() => Task.FromResult<Stream>(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "items.json"))));
    }
}
