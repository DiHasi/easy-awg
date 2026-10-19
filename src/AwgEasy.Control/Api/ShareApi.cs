using System.Text;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>Public share links. Anonymous by design - the point is to hand a config to someone who
/// has no account - so knowing the token is the only credential, and it unlocks nothing else.</summary>

public static class ShareApi
{
    public static void MapShares(this RouteGroupBuilder api)
    {
        // A link resolves only while it names the generation it was made for. The token travelled
        // to whoever the config was for over whatever channel was at hand, so once the identity it
        // named has been rotated - because a server was read - that link must not be what hands
        // the replacement key material to whoever else ended up holding it.
        api.MapGet("/shares/{token}", (string token, ShareRepository shares, ClientRepository clients, FleetService fleet) =>
        {
            var share = shares.Resolve(token, DateTimeOffset.UtcNow, fleet.Current.Generation);
            var client = share is null ? null : clients.Find(share.ClientId);

            return client is null
                ? Results.NotFound(new ApiError("share_not_found", "This link is not valid or has expired."))
                : Results.Ok(new PublicShareResponse(share!.ExpiresAt));
        }).AllowAnonymous();

        api.MapGet("/shares/{token}/config", (string token, ShareRepository shares, ClientRepository clients, FleetService fleet) =>
        {
            var current = fleet.Current;
            var share = shares.Resolve(token, DateTimeOffset.UtcNow, current.Generation);
            var client = share is null ? null : clients.Find(share.ClientId);
            if (client is null)
            {
                return Results.NotFound(new ApiError("share_not_found", "This link is not valid or has expired."));
            }

            var config = ClientConfigRenderer.Render(current, client, current.EndpointHost);
            // The person on the other end of the link now holds this generation's config, which
            // is the same fact the panel records when an operator downloads it themselves.
            clients.StampConfigGeneration(client.Id, current.Generation);
            // Not the client name: the file lands in someone else's downloads folder, and the label
            // an operator filed them under is not theirs to read.
            return Results.File(Encoding.UTF8.GetBytes(config), ClientConfigRenderer.ContentType, "amneziawg.conf");
        }).AllowAnonymous();
    }
}
