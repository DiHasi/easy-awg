using System.Net;
using System.Text;
using AwgEasy.Control;
using Microsoft.Extensions.Logging.Abstractions;

namespace AwgEasy.Tests;

/// <summary>
/// The Cloudflare updater against a stand-in API. What matters here is less the happy path than
/// the refusals: a record in the wrong zone, or a name that answers through several records, must
/// stop the switch instead of quietly editing one record while clients follow another.
/// </summary>
public class CloudflareDnsUpdaterTests
{
    private const string Zone = "zone-123";

    private static readonly DnsRecordTarget Target = new("vpn.example.com", "203.0.113.10", 60);

    [Fact]
    public async Task Updates_the_single_existing_record()
    {
        var api = new StubCloudflare().WithZone("example.com").WithRecords(Record("rec-1", "A", "198.51.100.1"));

        var result = await Updater(api).PointAsync(Target, CancellationToken.None);

        Assert.Equal(DnsUpdateOutcome.Applied, result.Outcome);
        var write = Assert.Single(api.Writes);
        Assert.Equal(HttpMethod.Patch, write.Method);
        Assert.EndsWith($"zones/{Zone}/dns_records/rec-1", write.Path);
        Assert.Contains("\"content\":\"203.0.113.10\"", write.Body);
        Assert.Contains("\"proxied\":false", write.Body);
    }

    [Fact]
    public async Task Creates_the_record_when_the_name_has_none()
    {
        var api = new StubCloudflare().WithZone("example.com").WithRecords();

        var result = await Updater(api).PointAsync(Target, CancellationToken.None);

        Assert.Equal(DnsUpdateOutcome.Applied, result.Outcome);
        Assert.Equal(HttpMethod.Post, Assert.Single(api.Writes).Method);
    }

    // A zone id copied from another zone: the record would be created where nothing delegates to
    // it, and the switch would report success while every client stayed put.
    [Fact]
    public async Task Refuses_a_name_outside_the_configured_zone()
    {
        var api = new StubCloudflare().WithZone("example.org").WithRecords();

        var result = await Updater(api).PointAsync(Target, CancellationToken.None);

        Assert.Equal(DnsUpdateOutcome.Failed, result.Outcome);
        Assert.Equal("dns_zone_mismatch", result.Error!.Code);
        Assert.Empty(api.Writes);
    }

    // "example.com" must not accept "vpn.badexample.com" just because the text ends the same way.
    [Fact]
    public async Task Matches_the_zone_on_a_label_boundary()
    {
        var api = new StubCloudflare().WithZone("example.com").WithRecords();

        var result = await Updater(api).PointAsync(Target with { Name = "vpn.badexample.com" }, CancellationToken.None);

        Assert.Equal("dns_zone_mismatch", result.Error!.Code);
    }

    [Fact]
    public async Task Accepts_the_zone_apex_itself()
    {
        var api = new StubCloudflare().WithZone("example.com").WithRecords();

        var result = await Updater(api).PointAsync(Target with { Name = "example.com" }, CancellationToken.None);

        Assert.Equal(DnsUpdateOutcome.Applied, result.Outcome);
    }

    // The old behaviour took the first of several: one record moved, the others kept answering,
    // and resolvers handed clients the old node as often as the new one.
    [Fact]
    public async Task Refuses_a_name_that_answers_through_several_records()
    {
        var api = new StubCloudflare().WithZone("example.com").WithRecords(
            Record("rec-1", "A", "198.51.100.1"),
            Record("rec-2", "A", "198.51.100.2"));

        var result = await Updater(api).PointAsync(Target, CancellationToken.None);

        Assert.Equal(DnsUpdateOutcome.Failed, result.Outcome);
        Assert.Equal("dns_record_ambiguous", result.Error!.Code);
        Assert.Contains("198.51.100.2", result.Error.Message);
        Assert.Empty(api.Writes);
    }

    [Fact]
    public async Task Refuses_to_move_the_a_record_while_an_aaaa_record_keeps_answering()
    {
        var api = new StubCloudflare().WithZone("example.com").WithRecords(Record("rec-6", "AAAA", "2001:db8::1"));

        var result = await Updater(api).PointAsync(Target, CancellationToken.None);

        Assert.Equal("dns_record_conflict", result.Error!.Code);
        Assert.Empty(api.Writes);
    }

    [Fact]
    public async Task Refuses_a_name_that_is_a_cname()
    {
        var api = new StubCloudflare().WithZone("example.com").WithRecords(Record("rec-c", "CNAME", "edge.example.net"));

        var result = await Updater(api).PointAsync(Target, CancellationToken.None);

        Assert.Equal("dns_record_conflict", result.Error!.Code);
        Assert.Empty(api.Writes);
    }

    // An unreadable listing is not an empty one. Creating a record here would put a second one
    // beside the existing record, and the name would answer with both nodes.
    [Fact]
    public async Task Does_not_create_a_record_when_the_listing_fails()
    {
        var api = new StubCloudflare().WithZone("example.com").FailListing();

        var result = await Updater(api).PointAsync(Target, CancellationToken.None);

        Assert.Equal(DnsUpdateOutcome.Failed, result.Outcome);
        Assert.Empty(api.Writes);
    }

    [Fact]
    public async Task Explains_a_token_that_cannot_read_the_zone()
    {
        var api = new StubCloudflare().ForbidZone();

        var result = await Updater(api).PointAsync(Target, CancellationToken.None);

        Assert.Equal("dns_zone_unreadable", result.Error!.Code);
        Assert.Contains("Zone:Read", result.Error.Message);
    }

    [Fact]
    public async Task Checks_without_writing_anything()
    {
        var healthy = new StubCloudflare().WithZone("example.com").WithRecords(Record("rec-1", "A", "198.51.100.1"));
        var ambiguous = new StubCloudflare().WithZone("example.com").WithRecords(
            Record("rec-1", "A", "198.51.100.1"),
            Record("rec-2", "A", "198.51.100.2"));

        Assert.Null(await Updater(healthy).CheckAsync("vpn.example.com", CancellationToken.None));
        Assert.Equal("dns_record_ambiguous", (await Updater(ambiguous).CheckAsync("vpn.example.com", CancellationToken.None))!.Code);
        Assert.Empty(healthy.Writes);
        Assert.Empty(ambiguous.Writes);
    }

    private static CloudflareDnsUpdater Updater(StubCloudflare api)
    {
        var http = new HttpClient(api) { BaseAddress = new Uri("https://api.cloudflare.test/client/v4/") };
        var options = new ControlOptions(
            DatabasePath: "unused.db",
            DefaultSubnet: "10.8.0.0/24",
            DefaultListenPort: 51820,
            DefaultClientAllowedIps: "0.0.0.0/0",
            DefaultClientDns: null,
            DefaultEndpointHost: "vpn.example.com",
            BundleLifetime: TimeSpan.FromMinutes(15),
            BootstrapAdminUser: null,
            BootstrapAdminPassword: null,
            LegacyStateImportPath: null,
            Dns: new DnsFailoverOptions(null, 60, "token", Zone),
            Failover: FailoverOptions.FromEnvironment(_ => null, (_, fallback) => fallback),
            Notifications: new NotificationOptions(null, null, null));

        return new CloudflareDnsUpdater(http, options, NullLogger<CloudflareDnsUpdater>.Instance);
    }

    private static string Record(string id, string type, string content)
        => $$"""{"id":"{{id}}","name":"vpn.example.com","type":"{{type}}","content":"{{content}}","ttl":60}""";

    private sealed record Write(HttpMethod Method, string Path, string Body);

    /// <summary>Answers the three calls the updater makes: the zone, the listing, and the write.</summary>
    private sealed class StubCloudflare : HttpMessageHandler
    {
        private string? _zoneName;
        private bool _forbidZone;
        private string[] _records = [];
        private bool _failListing;

        public List<Write> Writes { get; } = [];

        public StubCloudflare WithZone(string name)
        {
            _zoneName = name;
            return this;
        }

        public StubCloudflare ForbidZone()
        {
            _forbidZone = true;
            return this;
        }

        public StubCloudflare WithRecords(params string[] records)
        {
            _records = records;
            return this;
        }

        public StubCloudflare FailListing()
        {
            _failListing = true;
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get && path.EndsWith($"zones/{Zone}", StringComparison.Ordinal))
            {
                return _forbidZone
                    ? Json(HttpStatusCode.Forbidden, """{"success":false,"errors":[],"result":null}""")
                    : Json(HttpStatusCode.OK, $$"""{"success":true,"errors":[],"result":{"id":"{{Zone}}","name":"{{_zoneName}}" """ + "}}");
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/dns_records", StringComparison.Ordinal))
            {
                return _failListing
                    ? Json(HttpStatusCode.InternalServerError, "<html>upstream error</html>")
                    : Json(HttpStatusCode.OK, $$"""{"success":true,"errors":[],"result":[{{string.Join(",", _records)}}]}""");
            }

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Writes.Add(new Write(request.Method, path, body));
            return Json(HttpStatusCode.OK, """{"success":true,"errors":[],"result":""" + Record("rec-new", "A", "203.0.113.10") + "}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
