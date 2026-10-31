using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Who can sign in to this panel.
///
/// It exists because the bootstrap variables only ever create an account on an empty database, so
/// until now rotating the panel's password - after a host was found compromised, say - meant
/// editing SQLite on that host by hand, and adding a second operator was not possible at all.
///
/// Under <c>/api/admins</c> rather than <c>/api/auth</c> deliberately. Both guards let the auth
/// group through in any method, because signing in and out has to work on a standby and for the
/// demo account alike; a write placed there would be a write the read-only account could make and
/// one a standby would apply to a database the next snapshot replaces. Here it is refused by both
/// without either having to name it, which is the whole point of keeping those rules in one place.
///
/// The accounts themselves replicate: they are in the snapshot beside the fleet, because a
/// promoted standby that did not know who its operators were would be a panel nobody could sign
/// in to.
/// </summary>
public static class AdminAccountApi
{
    public static void MapAdmins(this RouteGroupBuilder admin)
    {
        admin.MapGet("/admins", (AdminAccounts accounts, HttpContext context) =>
        {
            var self = context.User.Identity?.Name;
            return TypedResults.Ok(accounts.List()
                .Select(account => new AdminResponse(
                    account.Username,
                    account.IsDemo,
                    string.Equals(account.Username, self, StringComparison.OrdinalIgnoreCase),
                    account.CreatedAt))
                .ToArray());
        });

        // A full operator every time. The read-only account stays configured through
        // AWG_DEMO_USER, for the reason it was put there: a demo account is a password published
        // next to a link, and the decision to have one at all belongs with the deployment rather
        // than behind a button somebody could press by accident.
        admin.MapPost("/admins", (
            CreateAdminRequest request,
            AdminAccounts accounts,
            HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username))
            {
                return Results.BadRequest(new ApiError("admin_username_required", "A username is required."));
            }

            var username = request.Username.Trim();
            if (accounts.Exists(username))
            {
                return Results.BadRequest(new ApiError("admin_exists", "An account with this name already exists."));
            }

            if (AdminAccounts.ValidatePassword(request.Password) is { } weak)
            {
                return Results.BadRequest(weak);
            }

            accounts.Create(username, request.Password, demo: false, actor: context.User.Identity?.Name);
            return Results.Created($"/api/admins/{username}", new AdminResponse(username, false, false, DateTimeOffset.UtcNow));
        });

        // Your own, and it needs the current password. The session cookie is not enough on
        // purpose: a stolen cookie can already do a great deal, but it must not be able to take
        // the account itself over and lock the operator out of their own fleet. It is also the
        // one write here an operator can make about themselves, which is why it is not keyed by
        // name - there is no way to aim it at somebody else by mistyping.
        admin.MapPost("/admins/password", (
            ChangePasswordRequest request,
            AdminAccounts accounts,
            LoginThrottle throttle,
            EventLog events,
            HttpContext context) =>
        {
            var username = context.User.Identity?.Name;
            if (string.IsNullOrEmpty(username))
            {
                return Results.Unauthorized();
            }

            // The same brake the sign-in endpoint has, counted against the same account: without
            // it a stolen cookie would be an unlimited oracle for the password it does not know.
            var now = DateTimeOffset.UtcNow;
            var address = context.SourceAddress();
            if (throttle.Check(username, address, now) is { Allowed: false })
            {
                return Results.BadRequest(new ApiError(
                    "login_throttled",
                    "Too many failed attempts. Wait for them to age out before trying again."));
            }

            if (!accounts.Verify(username, request.CurrentPassword))
            {
                throttle.RecordFailure(username, address, now);
                events.Record("admin.password_rejected", $"A wrong current password was given for {username}.", actor: username);
                return Results.BadRequest(new ApiError("password_incorrect", "The current password is not right."));
            }

            if (AdminAccounts.ValidatePassword(request.NewPassword) is { } weak)
            {
                return Results.BadRequest(weak);
            }

            throttle.RecordSuccess(username, address);
            accounts.ChangePassword(username, request.NewPassword, actor: username);

            // Deliberately no sign-out, here or anywhere: the cookie carries a name and nothing
            // derived from the password, so every session that was open stays open - this one
            // included. Worth knowing rather than papering over, because an operator rotating a
            // password after a compromise is rotating what gets somebody *in*, and a cookie
            // already taken keeps working until it expires.
            return Results.Ok(new HealthResponse("ok"));
        });

        // Somebody else's, without their current password, because an operator who has lost one
        // has nothing to present - that is what makes a second operator worth having. Never your
        // own: that goes through the endpoint above, where the current password is required.
        admin.MapPut("/admins/{username}/password", (
            string username,
            ResetPasswordRequest request,
            AdminAccounts accounts,
            HttpContext context) =>
        {
            var self = context.User.Identity?.Name;
            if (string.Equals(username, self, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new ApiError(
                    "admin_is_self",
                    "Changing your own password needs the current one. Use the change-password form instead."));
            }

            if (AdminAccounts.ValidatePassword(request.Password) is { } weak)
            {
                return Results.BadRequest(weak);
            }

            return accounts.ChangePassword(username, request.Password, actor: self)
                ? Results.Ok(new HealthResponse("ok"))
                : Results.NotFound(new ApiError("admin_not_found", "There is no account with this name."));
        });

        admin.MapDelete("/admins/{username}", (
            string username,
            AdminAccounts accounts,
            HttpContext context) =>
        {
            // Never your own, and that single rule is also what keeps the panel from ever ending
            // up with nobody who can administer it: removing an account that is not the one
            // asking means there are at least two, and the demo account cannot ask at all. A
            // panel with no operator could not be repaired from the panel - the bootstrap
            // variables are ignored once there is a database - so it would mean editing SQLite on
            // the host, which is the state this whole endpoint exists to get an operator out of.
            if (string.Equals(username, context.User.Identity?.Name, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new ApiError(
                    "admin_is_self",
                    "You cannot remove the account you are signed in with - it is also the last thing stopping this "
                    + "panel from having no operator at all. Sign in as another operator and remove it from there."));
            }

            return accounts.Delete(username, actor: context.User.Identity?.Name)
                ? Results.NoContent()
                : Results.NotFound(new ApiError("admin_not_found", "There is no account with this name."));
        });
    }
}
