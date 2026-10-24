using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// The panel pair: what state it is in, the two acts that change which panel is in charge, and
/// the replication endpoints the other panel talks to.
///
/// The admin half is deliberately small. Everything about a panel other than its name lives in
/// that panel's own environment, because a panel is not configured from here - the whole point of
/// a standby is that it can be stood up from nothing but an image, a token and three variables.
/// </summary>
public static class PanelApi
{
    public static void MapPanel(this RouteGroupBuilder admin)
    {
        admin.MapGet("/panel", (PanelPairDescriber describer) => TypedResults.Ok(describer.Describe()));

        admin.MapGet("/panels", (PanelPairDescriber describer) => TypedResults.Ok(describer.Panels()));

        // Issuing this hands the other machine the fleet private key, every client private key and
        // every admin credential, so the warning is part of the response rather than a line in the
        // README: a caller driving the API has to hear it too.
        admin.MapPost("/panels/tokens", (
            CreatePanelRequest request,
            EnrollmentService enrollment,
            PanelContext context,
            ControlOptions options,
            HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new ApiError("panel_name_required", "Panel name is required."));
            }

            if (!context.IsActive)
            {
                return Results.BadRequest(new ApiError(
                    "panel_not_active",
                    "Only the panel in charge can enroll a standby. Issue the token there."));
            }

            // A pair that cannot watch itself has no fencing, and the fencing is what makes a
            // promotion survivable. Refused here rather than discovered the first time two panels
            // both believe they are in charge.
            if (string.IsNullOrWhiteSpace(options.Panel.Url))
            {
                return Results.BadRequest(new ApiError(
                    "panel_url_required",
                    "AWG_PANEL_URL is not set on this panel, so a standby would have no way to reach it and this "
                    + "panel would have no way to find out it had been superseded. Set it to this panel's own base "
                    + "URL and restart before enrolling a standby."));
            }

            var controlUrl = options.Panel.Url ?? $"{http.Request.Scheme}://{http.Request.Host}";
            return Results.Ok(enrollment.CreateToken(request.Name.Trim(), controlUrl, EnrollmentService.PanelToken));
        });

        admin.MapPut("/panel/failover", async Task<IResult> (
            UpdatePanelFailoverRequest request,
            PanelRepository panels,
            PanelContext context,
            PanelPairDescriber describer,
            IPanelWitness witness,
            ControlOptions options,
            EventLog events,
            HttpContext http) =>
        {
            var mode = request.Mode?.Trim().ToLowerInvariant();
            if (!PanelFailoverModes.IsKnown(mode))
            {
                return Results.BadRequest(new ApiError("panel_mode_invalid", "Mode must be 'manual' or 'automatic'."));
            }

            if (mode == PanelFailoverModes.Automatic)
            {
                if (panels.List().Count(panel => !panel.Revoked) <= 1)
                {
                    return Results.BadRequest(new ApiError(
                        "panel_no_standby",
                        "There is no standby panel to promote. Enroll one first."));
                }

                // Without something outside the pair to ask, an armed standby cannot tell the
                // panel in charge being gone from its own network being gone - so arming it would
                // buy nothing and risk a promotion made on no evidence. Said now, while somebody
                // is looking, rather than at the moment the other panel dies.
                if (!witness.IsConfigured)
                {
                    return Results.BadRequest(new ApiError(
                        "panel_witness_required",
                        "Automatic promotion needs a witness: AWG_PANEL_WITNESS_URLS is switched off, so a standby "
                        + "could not tell this panel being gone from its own uplink being gone. Leave promotion manual, "
                        + "or give the standby witness URLs it can reach."));
                }
            }

            panels.SetFailoverMode(mode!, DateTimeOffset.UtcNow);
            events.Record(
                "panel.failover_mode",
                mode == PanelFailoverModes.Automatic
                    ? $"Automatic panel promotion armed: a standby takes over after {PanelFailoverPolicy.Describe(options.Panel.Grace)} "
                      + "without contact, if its own uplink still works."
                    : "Automatic panel promotion disarmed: switching panels is manual.",
                actor: http.User.Identity?.Name);

            await Task.CompletedTask;
            return Results.Ok(describer.Describe());
        });

        // The two acts. Both are reachable on a standby, which is the whole point: the panel an
        // operator can still log into is the one that has to be able to take over.
        admin.MapPost("/panel/promote", async Task<IResult> (
            PanelPromotionService promotion,
            PanelPairDescriber describer,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            var (outcome, error) = await promotion.PromoteAsync(
                http.User.Identity?.Name,
                "promoted by hand",
                cancellationToken);

            return outcome is null
                ? Results.BadRequest(error)
                : Results.Ok(new PanelSwitchResponse(outcome.Term, outcome.RecordWarning, describer.Describe()));
        });

        admin.MapPost("/panel/demote", async Task<IResult> (
            PanelPromotionService promotion,
            PanelPairDescriber describer,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            var (demoted, error) = await promotion.DemoteAsync(
                http.User.Identity?.Name,
                "stepped down by hand",
                observedTerm: null,
                observedPanelId: null,
                cancellationToken);

            return demoted
                ? Results.Ok(describer.Describe())
                : Results.BadRequest(error);
        });

        // Writes only this panel's own local row, which is why a standby may do it: it is the
        // brake, and a brake that needs the panel in charge to be reachable is not one.
        admin.MapPut("/panel/hold", (
            PanelHoldRequest request,
            PanelLocalStore store,
            PanelContext context,
            PanelPairDescriber describer,
            EventLog events,
            HttpContext http) =>
        {
            store.SetHold(request.Held);
            context.Reload();
            events.Record(
                request.Held ? "panel.held" : "panel.released",
                request.Held
                    ? "This panel is held: it will not promote itself whatever the pair says."
                    : "Hold released: this panel may promote itself again if automatic promotion is armed.",
                actor: http.User.Identity?.Name);

            return TypedResults.Ok(describer.Describe());
        });

        // Runs a round now rather than at the next tick. What an operator presses after fixing
        // something, and what lets the test suite drive replication without waiting on a timer.
        admin.MapPost("/panel/sync", async (
            PanelPairService pair,
            PanelPairDescriber describer,
            CancellationToken cancellationToken) =>
        {
            await pair.EvaluateOnceAsync(cancellationToken);
            return TypedResults.Ok(describer.Describe());
        });

        admin.MapPost("/panels/{id}/revoke", (string id, PanelRepository panels, PanelContext context, EventLog events, HttpContext http) =>
        {
            if (string.Equals(id, context.PanelId, StringComparison.Ordinal))
            {
                return Results.BadRequest(new ApiError("panel_self", "A panel cannot revoke itself."));
            }

            if (!panels.SetRevoked(id, true))
            {
                return Results.NotFound(new ApiError("panel_not_found", "Panel was not found."));
            }

            // Checked on every request from it, so it stops receiving the fleet on the very next
            // poll. What it already holds it keeps - a revocation is not a remote wipe, and the
            // panel state it has is as current as the moment it was cut off.
            events.Record(
                "panel.revoked",
                "Standby panel revoked: it receives no further configuration. What it already holds, it keeps.",
                actor: http.User.Identity?.Name);

            return Results.NoContent();
        });

        admin.MapDelete("/panels/{id}", (string id, PanelRepository panels, PanelContext context, EventLog events, HttpContext http) =>
        {
            if (string.Equals(id, context.PanelId, StringComparison.Ordinal))
            {
                return Results.BadRequest(new ApiError("panel_self", "A panel cannot delete itself."));
            }

            if (!panels.Delete(id))
            {
                return Results.NotFound(new ApiError("panel_not_found", "Panel was not found."));
            }

            events.Record("panel.deleted", "Standby panel removed from the pair.", actor: http.User.Identity?.Name);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// The replication endpoints, beside the agents' and the probes' and authenticated the same
    /// way. What is different is what they hand over: an agent gets public keys and addresses, a
    /// probe gets one client config, and these hand over the whole panel.
    /// </summary>
    public static void MapPanelReplicationApi(this WebApplication app)
    {
        var panels = app.MapGroup("/api/v1/panels").ExcludeFromDescription();

        panels.MapPost("/enroll", (EnrollRequest request, EnrollmentService enrollment, PanelContext context) =>
        {
            if (!context.IsActive)
            {
                return Results.Json(
                    new ApiError("panel_not_active", "This panel is a standby and cannot enroll another panel."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var (response, error) = enrollment.EnrollPanel(request);
            return response is null ? Results.BadRequest(error) : Results.Ok(response);
        });

        panels.MapGet("/{panelId}/snapshot", async Task<IResult> (
            string panelId,
            HttpContext http,
            AgentAuthenticator authenticator,
            PanelContext context,
            PanelRepository repository,
            PanelSnapshotRepository snapshots,
            FleetService fleet,
            ControlOptions options) =>
        {
            if (await Authorize(http, authenticator, panelId, context) is { } refusal)
            {
                return refusal;
            }

            // The ETag is taken over the snapshot with its stamps cleared, so an unchanged fleet
            // answers 304 instead of transferring itself every thirty seconds. A stamped payload
            // would differ on every call and the gate would never close.
            var unstamped = snapshots.BuildSnapshot(panelId);
            var etag = $"\"{PanelSigning.Fingerprint(unstamped, ControlJsonContext.Default.PanelSnapshot)}\"";
            if (http.Request.Headers.IfNoneMatch.ToString() == etag)
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            var now = DateTimeOffset.UtcNow;
            var stamped = unstamped with { CreatedAt = now, ExpiresAt = now.Add(options.BundleLifetime) };

            http.Response.Headers.ETag = etag;
            return Results.Ok(Sign(fleet, stamped, ControlJsonContext.Default.PanelSnapshot));
        });

        // A POST for what is a read: the cursors decide which history this panel is handed, and in
        // the body they are covered by the request signature where a query string would not be.
        //
        // The body is read here rather than bound as a parameter, exactly as the agent's status
        // report is. Binding happens before the handler runs, which would consume the stream the
        // signature check has to hash - and the request would be refused as unsigned.
        panels.MapPost("/{panelId}/stream", async Task<IResult> (
            string panelId,
            HttpContext http,
            AgentAuthenticator authenticator,
            PanelContext context,
            PanelSnapshotRepository snapshots,
            FleetService fleet,
            ControlOptions options) =>
        {
            if (await Authorize(http, authenticator, panelId, context) is { } refusal)
            {
                return refusal;
            }

            var cursors = await http.Request.ReadFromJsonAsync<PanelStreamCursors>();
            var now = DateTimeOffset.UtcNow;
            var stream = snapshots.BuildStream(panelId, cursors ?? PanelStreamCursors.Beginning, options.Panel.StreamBatchSize)
                with { CreatedAt = now, ExpiresAt = now.Add(options.BundleLifetime) };

            return Results.Ok(Sign(fleet, stream, ControlJsonContext.Default.PanelStream));
        });

        panels.MapPost("/{panelId}/status", async Task<IResult> (
            string panelId,
            HttpContext http,
            AgentAuthenticator authenticator,
            PanelContext context,
            PanelRepository repository,
            FleetRepository fleet) =>
        {
            if (await Authorize(http, authenticator, panelId, context) is { } refusal)
            {
                return refusal;
            }

            var report = await http.Request.ReadFromJsonAsync<PanelStatusReport>();
            if (report is null)
            {
                return Results.BadRequest(new ApiError("panel_status_invalid", "Status report body is required."));
            }

            repository.RecordState(panelId, report, DateTimeOffset.UtcNow);
            return Results.Ok(Hello(context, repository, fleet));
        });

        // Answered in either role, and that is load-bearing: this is how a panel that lost a
        // promotion finds out. A superseded panel that refused to say which term it was at could
        // never be told it had been superseded.
        panels.MapGet("/hello", async Task<IResult> (
            HttpContext http,
            AgentAuthenticator authenticator,
            PanelContext context,
            PanelRepository repository,
            FleetRepository fleet) =>
        {
            var caller = await authenticator.AuthenticatePanelAsync(http);
            return caller is null
                ? Results.Unauthorized()
                : Results.Ok(Hello(context, repository, fleet));
        });
    }

    /// <summary>
    /// A signed request proves who the caller is; these three checks decide whether it may be
    /// handed the fleet. A standby refuses outright - two panels serving snapshots would be two
    /// places a fleet is decided.
    /// </summary>
    private static async Task<IResult?> Authorize(
        HttpContext http,
        AgentAuthenticator authenticator,
        string panelId,
        PanelContext context)
    {
        var caller = await authenticator.AuthenticatePanelAsync(http);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        if (!string.Equals(caller.Id, panelId, StringComparison.Ordinal))
        {
            return Results.Forbid();
        }

        if (!context.IsActive)
        {
            return Results.Json(
                new ApiError(
                    "panel_not_active",
                    "This panel is a standby: it replicates the fleet rather than publishing it. Pull from the panel in charge."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return null;
    }

    private static SignedPanelPayload Sign<T>(FleetService fleet, T payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var current = fleet.Current;
        using var key = BundleSigning.ImportPrivateKey(current.SigningPrivateKey);
        return PanelSigning.Sign(payload, key, current.SigningKeyId, typeInfo);
    }

    private static PanelHello Hello(PanelContext context, PanelRepository panels, FleetRepository fleet)
    {
        var state = context.State;
        var pair = panels.FindPairState();
        var self = panels.Find(state.PanelId);

        return new PanelHello(
            state.PanelId,
            self?.Name ?? state.PanelId,
            state.Role,
            // The fleet term, not the local one: what the pair compares is which term the panel is
            // serving, and on a standby that is whatever the last snapshot carried.
            Math.Max(pair.Term, state.PromotedTerm),
            pair.ActivePanelId,
            state.SnapshotAt,
            PanelVersion.Current,
            // Null on a panel that has never synced, which is a real state and not an error.
            fleet.Find()?.Revision ?? 0);
    }
}
