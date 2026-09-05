using AwgEasy.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AwgEasy.Control;

public static class AgentApi
{
    public static void MapAgentApi(this WebApplication app)
    {
        var agents = app.MapGroup("/api/v1/agents").ExcludeFromDescription();

        // Enrollment is the one agent call that cannot be signature-authenticated: the node has
        // no identity yet. It is gated by the one-time token instead.
        agents.MapPost("/enroll", (EnrollRequest request, EnrollmentService enrollment) =>
        {
            var (response, error) = enrollment.Enroll(request);
            return response is null
                ? Results.BadRequest(error)
                : Results.Ok(response);
        });

        agents.MapGet("/{nodeId}/desired", async Task<IResult> (
            string nodeId,
            HttpContext context,
            AgentAuthenticator authenticator,
            FleetService fleet,
            NodeRepository nodes,
            EventLog events) =>
        {
            var auth = await authenticator.AuthenticateAsync(context);
            if (!auth.Succeeded || auth.Node is null)
            {
                return Results.Unauthorized();
            }

            // A signed request proves who the caller is, but not that it may act for this path.
            if (!string.Equals(auth.Node.Id, nodeId, StringComparison.Ordinal))
            {
                return Results.Forbid();
            }

            var current = fleet.Current;
            var etag = $"\"rev-{current.Revision}\"";
            if (context.Request.Headers.IfNoneMatch.ToString() == etag)
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            context.Response.Headers.ETag = etag;
            events.Record("node.bundle_fetched", $"Node '{auth.Node.Name}' fetched revision {current.Revision}.", nodeId: auth.Node.Id);
            return Results.Ok(fleet.BuildBundle(auth.Node));
        });

        agents.MapPost("/{nodeId}/status", async Task<IResult> (
            string nodeId,
            HttpContext context,
            AgentAuthenticator authenticator,
            NodeRepository nodes,
            FleetService fleet) =>
        {
            var auth = await authenticator.AuthenticateAsync(context);
            if (!auth.Succeeded || auth.Node is null)
            {
                return Results.Unauthorized();
            }

            if (!string.Equals(auth.Node.Id, nodeId, StringComparison.Ordinal))
            {
                return Results.Forbid();
            }

            var report = await context.Request.ReadFromJsonAsync<NodeStatusReport>();
            if (report is null)
            {
                return Results.BadRequest(new ApiError("status_invalid", "Status report body is required."));
            }

            var current = fleet.Current;
            nodes.RecordStatus(auth.Node.Id, report, DeriveStatus(report));
            nodes.ReplacePeerStats(auth.Node.Id, report.Peers, report.ReportedAt);

            return Results.Ok(new NodeStatusAck(current.Revision, report.AppliedRevision != current.Revision));
        });
    }

    /// <summary>
    /// A node that serves traffic while failing to reach the control plane is degraded, not down.
    /// Keeping that distinction is what stops a control-plane hiccup from looking like an outage.
    /// </summary>
    private static string DeriveStatus(NodeStatusReport report)
        => report.InterfaceUp
            ? report.LastError is null ? NodeStatuses.Healthy : NodeStatuses.Degraded
            : NodeStatuses.Down;
}
