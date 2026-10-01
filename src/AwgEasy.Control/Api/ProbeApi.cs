using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>Probe management: enrollment tokens, what each probe last saw, revocation.</summary>
public static class ProbeApi
{
    public static void MapProbes(this RouteGroupBuilder admin)
    {
        admin.MapGet("/probes", (ProbeService probes) => TypedResults.Ok(probes.Describe()));

        admin.MapPost("/probes/tokens", (CreateProbeRequest request, EnrollmentService enrollment, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new ApiError("probe_name_required", "Probe name is required."));
            }

            var controlUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            return Results.Ok(enrollment.CreateToken(request.Name.Trim(), controlUrl, EnrollmentService.ProbeToken));
        });

        admin.MapPost("/probes/{id}/revoke", (string id, ProbeService probes, HttpContext context)
            => probes.Revoke(id, context.User.Identity?.Name)
                ? Results.NoContent()
                : Results.NotFound(new ApiError("probe_not_found", "Probe was not found.")));

        admin.MapDelete("/probes/{id}", (string id, ProbeService probes, HttpContext context)
            => probes.Delete(id, context.User.Identity?.Name)
                ? Results.NoContent()
                : Results.NotFound(new ApiError("probe_not_found", "Probe was not found.")));
    }

    /// <summary>
    /// The probe's side, beside the agents' and authenticated the same way: by a signature over
    /// the request, checked against a key the probe generated and registered at enrollment.
    /// </summary>
    public static void MapProbeAgentApi(this WebApplication app)
    {
        var probes = app.MapGroup("/api/v1/probes").ExcludeFromDescription();

        probes.MapPost("/enroll", (EnrollRequest request, EnrollmentService enrollment) =>
        {
            var (response, error) = enrollment.EnrollProbe(request);
            return response is null ? Results.BadRequest(error) : Results.Ok(response);
        });

        probes.MapGet("/{probeId}/assignment", async Task<IResult> (
            string probeId,
            HttpContext context,
            AgentAuthenticator authenticator,
            ProbeService service) =>
        {
            var probe = await authenticator.AuthenticateProbeAsync(context);
            if (probe is null)
            {
                return Results.Unauthorized();
            }

            if (!string.Equals(probe.Id, probeId, StringComparison.Ordinal))
            {
                return Results.Forbid();
            }

            return Results.Ok(service.BuildAssignment(probe));
        });

        probes.MapPost("/{probeId}/results", async Task<IResult> (
            string probeId,
            HttpContext context,
            AgentAuthenticator authenticator,
            ProbeRepository repository) =>
        {
            var probe = await authenticator.AuthenticateProbeAsync(context);
            if (probe is null)
            {
                return Results.Unauthorized();
            }

            if (!string.Equals(probe.Id, probeId, StringComparison.Ordinal))
            {
                return Results.Forbid();
            }

            var report = await context.Request.ReadFromJsonAsync<ProbeReport>();
            if (report is null)
            {
                return Results.BadRequest(new ApiError("probe_report_invalid", "Probe report body is required."));
            }

            // Results decide whether traffic moves, so anything malformed is refused whole
            // rather than half-stored.
            if (report.Results.Length > 256 || report.Results.Any(result =>
                    string.IsNullOrWhiteSpace(result.NodeId)
                    || string.IsNullOrWhiteSpace(result.Address)
                    || !ProbeOutcomes.IsKnown(result.Outcome)))
            {
                return Results.BadRequest(new ApiError("probe_report_invalid", "Probe report contains an invalid result."));
            }

            // The panel's clock, not the probe's: staleness is judged here, and a probe with a
            // wandering clock must not be able to keep its results fresh forever.
            var now = DateTimeOffset.UtcNow;
            repository.RecordRound(
                probe.Id,
                report.AgentVersion,
                report.Results.Select(result => result with { CheckedAt = now }),
                now);

            return Results.NoContent();
        });
    }
}
