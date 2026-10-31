using System.Net;
using System.Net.Http.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// Managing who can sign in.
///
/// It exists because the bootstrap variables only create an account on an empty database: before
/// this, changing the panel's password - after the host it ran on was found compromised, say -
/// meant editing SQLite by hand, and there was no way to have a second operator at all.
///
/// Every test here works on accounts it creates itself. The fixture's own admin is what the suite
/// signs in with, so a test that reset its password or removed it would take the rest of the class
/// down with it.
/// </summary>
public class AdminAccountTests(ControlPlaneFixture fixture) : IClassFixture<ControlPlaneFixture>
{
    private const string GoodPassword = "a-long-enough-password";

    [Fact]
    public async Task The_signed_in_account_is_marked_and_the_demo_one_is_not_an_operator()
    {
        var client = await fixture.CreateAdminClientAsync();

        var accounts = await client.GetFromJsonAsync<AdminResponse[]>("/api/admins");

        var self = Assert.Single(accounts!, account => account.Self);
        Assert.Equal(ControlPlaneFixture.AdminUser, self.Username);
        Assert.False(self.Demo);

        var demo = Assert.Single(accounts!, account => account.Demo);
        Assert.Equal(ControlPlaneFixture.DemoUser, demo.Username);
        Assert.False(demo.Self);
    }

    [Fact]
    public async Task A_second_operator_can_be_created_and_signs_in()
    {
        var client = await fixture.CreateAdminClientAsync();

        var created = await client.PostAsJsonAsync("/api/admins", new CreateAdminRequest("second-operator", GoodPassword));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var signIn = await fixture.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new LoginRequest("second-operator", GoodPassword));

        signIn.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_same_name_is_refused_whatever_its_case()
    {
        var client = await fixture.CreateAdminClientAsync();
        (await client.PostAsJsonAsync("/api/admins", new CreateAdminRequest("duplicate-operator", GoodPassword)))
            .EnsureSuccessStatusCode();

        var again = await client.PostAsJsonAsync("/api/admins", new CreateAdminRequest("Duplicate-Operator", GoodPassword));

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("admin_exists", (await again.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    /// <summary>
    /// The minimum is enforced where a password is chosen, which is every way in: an account the
    /// panel creates with a weak password is one the throttle alone has to hold the line for.
    /// </summary>
    [Fact]
    public async Task A_short_password_is_refused()
    {
        var client = await fixture.CreateAdminClientAsync();

        var response = await client.PostAsJsonAsync("/api/admins", new CreateAdminRequest("too-easy", "short"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("password_too_short", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    /// <summary>
    /// A session cookie is not proof of knowing the password, and taking the account over is
    /// precisely what a stolen one must not be able to do.
    /// </summary>
    [Fact]
    public async Task Changing_your_own_password_needs_the_current_one()
    {
        var admin = await fixture.CreateAdminClientAsync();
        (await admin.PostAsJsonAsync("/api/admins", new CreateAdminRequest("rotates-own", GoodPassword)))
            .EnsureSuccessStatusCode();

        var own = fixture.CreateClient();
        (await own.PostAsJsonAsync("/api/auth/login", new LoginRequest("rotates-own", GoodPassword)))
            .EnsureSuccessStatusCode();

        var wrong = await own.PostAsJsonAsync(
            "/api/admins/password",
            new ChangePasswordRequest("not-the-password", "a-replacement-password"));

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("password_incorrect", (await wrong.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var right = await own.PostAsJsonAsync(
            "/api/admins/password",
            new ChangePasswordRequest(GoodPassword, "a-replacement-password"));

        right.EnsureSuccessStatusCode();

        var fresh = fixture.CreateClient();
        (await fresh.PostAsJsonAsync("/api/auth/login", new LoginRequest("rotates-own", "a-replacement-password")))
            .EnsureSuccessStatusCode();

        var stale = await fixture.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new LoginRequest("rotates-own", GoodPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    /// <summary>Which is the whole reason a second operator is worth creating before it is needed:
    /// somebody who has lost their password has nothing left to present.</summary>
    [Fact]
    public async Task Another_operator_resets_a_password_without_knowing_it()
    {
        var admin = await fixture.CreateAdminClientAsync();
        (await admin.PostAsJsonAsync("/api/admins", new CreateAdminRequest("forgot-it", GoodPassword)))
            .EnsureSuccessStatusCode();

        var reset = await admin.PutAsJsonAsync("/api/admins/forgot-it/password", new ResetPasswordRequest("handed-over-in-person"));
        reset.EnsureSuccessStatusCode();

        (await fixture.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("forgot-it", "handed-over-in-person")))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Resetting_your_own_password_that_way_is_refused()
    {
        var client = await fixture.CreateAdminClientAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/admins/{ControlPlaneFixture.AdminUser}/password",
            new ResetPasswordRequest("would-skip-the-current-one"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("admin_is_self", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task Resetting_a_password_for_an_account_that_is_not_there_is_a_404()
    {
        var client = await fixture.CreateAdminClientAsync();

        var response = await client.PutAsJsonAsync("/api/admins/never-existed/password", new ResetPasswordRequest(GoodPassword));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A deleted account keeps its cookie, and a session whose row is gone reads as the demo one -
    /// so allowing this would hand an operator a panel of stand-in names rather than locking them
    /// out, which is worse than refusing.
    /// </summary>
    [Fact]
    public async Task You_cannot_remove_the_account_you_are_signed_in_with()
    {
        var client = await fixture.CreateAdminClientAsync();

        var response = await client.DeleteAsync($"/api/admins/{ControlPlaneFixture.AdminUser}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("admin_is_self", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task An_operator_can_be_removed_and_stops_signing_in()
    {
        var client = await fixture.CreateAdminClientAsync();
        (await client.PostAsJsonAsync("/api/admins", new CreateAdminRequest("passing-through", GoodPassword)))
            .EnsureSuccessStatusCode();

        var removed = await client.DeleteAsync("/api/admins/passing-through");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var signIn = await fixture.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new LoginRequest("passing-through", GoodPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
    }

    /// <summary>
    /// Removing an operator ends the session they already had, rather than leaving it working
    /// until the cookie expires tonight. That is the whole point of being able to remove one: the
    /// reason to do it is usually that their access is the problem.
    ///
    /// It is also why a removed row must not simply read as the demo account - which is what the
    /// panel answers for a name it cannot find - or the operator would be handed a panel of
    /// stand-in names instead of being signed out.
    /// </summary>
    [Fact]
    public async Task Removing_an_operator_ends_the_session_it_already_had()
    {
        var admin = await fixture.CreateAdminClientAsync();
        (await admin.PostAsJsonAsync("/api/admins", new CreateAdminRequest("already-signed-in", GoodPassword)))
            .EnsureSuccessStatusCode();

        var theirs = fixture.CreateClient();
        (await theirs.PostAsJsonAsync("/api/auth/login", new LoginRequest("already-signed-in", GoodPassword)))
            .EnsureSuccessStatusCode();

        (await theirs.GetAsync("/api/auth/me")).EnsureSuccessStatusCode();

        (await admin.DeleteAsync("/api/admins/already-signed-in")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await theirs.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await theirs.GetAsync("/api/clients")).StatusCode);
    }
}

/// <summary>
/// How many wrong passwords the panel will look at.
///
/// Without this the sign-in endpoint accepted guesses as fast as the network could deliver them,
/// which is what the panel found on a host that had been rooted: plain HTTP on the open internet,
/// one password, and no limit on the attempts against it.
/// </summary>
public class LoginThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_account_is_refused_once_its_failures_reach_the_limit()
    {
        var throttle = new LoginThrottle();

        for (var attempt = 0; attempt < LoginThrottle.AccountFailures - 1; attempt++)
        {
            Assert.False(throttle.RecordFailure("admin", null, Now));
            Assert.True(throttle.Check("admin", null, Now).Allowed);
        }

        // The failure that reaches the limit says so, which is what the one log line hangs on.
        Assert.True(throttle.RecordFailure("admin", null, Now));
        Assert.False(throttle.Check("admin", null, Now).Allowed);
    }

    /// <summary>
    /// The account counter is the one that has to hold, because an attacker working through a
    /// wordlist against the one account the panel has can come from anywhere - and the address is
    /// whatever the forwarded headers said.
    /// </summary>
    [Fact]
    public void One_account_being_refused_does_not_refuse_another()
    {
        var throttle = new LoginThrottle();

        for (var attempt = 0; attempt < LoginThrottle.AccountFailures; attempt++)
        {
            throttle.RecordFailure("admin", "203.0.113.7", Now);
        }

        Assert.False(throttle.Check("admin", "203.0.113.7", Now).Allowed);
        Assert.True(throttle.Check("someone-else", "198.51.100.4", Now).Allowed);
    }

    [Fact]
    public void An_address_spraying_names_is_refused_across_all_of_them()
    {
        var throttle = new LoginThrottle();

        for (var attempt = 0; attempt < LoginThrottle.AddressFailures; attempt++)
        {
            throttle.RecordFailure($"guess-{attempt}", "203.0.113.9", Now);
        }

        Assert.False(throttle.Check("guess-fresh", "203.0.113.9", Now).Allowed);
        Assert.True(throttle.Check("guess-fresh", "198.51.100.4", Now).Allowed);
    }

    /// <summary>
    /// The window slides, so a refusal is a wait rather than a lock. That matters in the one
    /// direction this is dangerous: a stranger who knows the account name can hold the operator
    /// out, and what makes that survivable is that it ends on its own.
    /// </summary>
    [Fact]
    public void The_refusal_ages_out()
    {
        var throttle = new LoginThrottle();

        for (var attempt = 0; attempt < LoginThrottle.AccountFailures; attempt++)
        {
            throttle.RecordFailure("admin", null, Now);
        }

        var refused = throttle.Check("admin", null, Now);
        Assert.False(refused.Allowed);
        Assert.True(refused.RetryAfter > TimeSpan.Zero);
        Assert.True(refused.RetryAfter <= LoginThrottle.Window);

        Assert.True(throttle.Check("admin", null, Now + LoginThrottle.Window).Allowed);
    }

    [Fact]
    public void A_correct_password_forgets_what_came_before_it()
    {
        var throttle = new LoginThrottle();

        for (var attempt = 0; attempt < LoginThrottle.AccountFailures - 1; attempt++)
        {
            throttle.RecordFailure("admin", "203.0.113.7", Now);
        }

        throttle.RecordSuccess("admin", "203.0.113.7");

        for (var attempt = 0; attempt < LoginThrottle.AccountFailures - 1; attempt++)
        {
            Assert.False(throttle.RecordFailure("admin", "203.0.113.7", Now));
        }

        Assert.True(throttle.Check("admin", "203.0.113.7", Now).Allowed);
    }
}

/// <summary>
/// The same brake through the endpoint it protects. Its own fixture, because locking an account
/// out is exactly what it asserts and the counters belong to the panel's process.
/// </summary>
public class LoginThrottleEndpointTests(ControlPlaneFixture fixture) : IClassFixture<ControlPlaneFixture>
{
    [Fact]
    public async Task Guessing_at_one_account_stops_being_verified_and_leaves_the_others_alone()
    {
        var client = fixture.CreateClient();

        for (var attempt = 0; attempt < LoginThrottle.AccountFailures; attempt++)
        {
            var refused = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("ghost", $"guess-{attempt}"));
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        var throttled = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("ghost", "one-more"));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal("login_throttled", (await throttled.Content.ReadFromJsonAsync<ApiError>())!.Code);
        // The wait is part of the answer: a caller cannot tell a wrong password from a refused
        // attempt otherwise, and the panel's own form has something worth saying about it.
        Assert.NotNull(throttled.Headers.RetryAfter);

        // And the operator still gets in, which is the half of this that would be easy to lose.
        (await fixture.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new LoginRequest(ControlPlaneFixture.AdminUser, ControlPlaneFixture.AdminPassword)))
            .EnsureSuccessStatusCode();
    }
}
