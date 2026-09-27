namespace AwgEasy.Control;

/// <summary>
/// Where the failover record points, and whether the internet agrees with the panel. Switching it
/// lives on the node it switches to, as <c>POST /nodes/{id}/activate</c>.
/// </summary>
public static class DnsApi
{
    public static void MapDnsFailover(this RouteGroupBuilder admin)
        => admin.MapGet("/dns", async (DnsFailoverService failover, CancellationToken cancellationToken)
            => TypedResults.Ok(await failover.DescribeAsync(cancellationToken)));
}
