using System.Text;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>Public share links. Anonymous by design - the point is to hand a config to someone who
/// has no account - so knowing the token is the only credential, and it unlocks nothing else.</summary>

public static class ShareApi
{
    public static void MapShares(this RouteGroupBuilder api)
    {
        api.MapGet("/shares/{token}", (string token, ShareRepository shares, ClientRepository clients) =>
        {
            var share = shares.Resolve(token, DateTimeOffset.UtcNow);
            var client = share is null ? null : clients.Find(share.ClientId);

            return client is null
                ? Results.NotFound(new ApiError("share_not_found", "This link is not valid or has expired."))
                : Results.Ok(new PublicShareResponse(client.Name, share!.ExpiresAt));
        }).AllowAnonymous();

        api.MapGet("/shares/{token}/config", (string token, ShareRepository shares, ClientRepository clients, FleetService fleet) =>
        {
            var share = shares.Resolve(token, DateTimeOffset.UtcNow);
            var client = share is null ? null : clients.Find(share.ClientId);
            if (client is null)
            {
                return Results.NotFound(new ApiError("share_not_found", "This link is not valid or has expired."));
            }

            var current = fleet.Current;
            var config = ClientConfigRenderer.Render(current, client, current.EndpointHost);
            return Results.File(Encoding.UTF8.GetBytes(config), "text/plain; charset=utf-8", ClientConfigRenderer.FileName(client.Name));
        }).AllowAnonymous();
    }
}
