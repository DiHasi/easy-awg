using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// Panel-pair settings for the suite. Read from nothing rather than from the environment, so a
/// developer who happens to have AWG_PANEL_* set does not get a different suite than CI does.
///
/// The two timings are deliberately long. The pair's loop is a background timer, and a test that
/// shares a fleet with a timer that might fire mid-assertion is a test that fails once a week for
/// no reason: with an hour between ticks it never fires, and every round a test cares about is the
/// one it asked for through <c>POST /api/panel/sync</c>. The grace period has to stay above the
/// interval - the panel refuses to start otherwise, and for good reason - so it is longer still,
/// which also keeps the automatic path out of the integration tests. That path is decided by
/// <see cref="AwgEasy.Control.PanelFailoverPolicy"/>, which is pure and tested directly.
/// </summary>
internal static class TestPanelOptions
{
    public static readonly PanelOptions Default = Active();

    /// <param name="witnessUrls">
    /// Empty by default. A test that wants an armed standby to take over has to say so, which is
    /// the right way round: automatic promotion refuses itself without a witness, and a suite that
    /// silently had real URLs in it would be reaching the internet to decide a test.
    /// </param>
    public static PanelOptions Active(
        string name = "panel-a",
        string? url = "http://panel-a.test",
        string? publicIp = "203.0.113.1",
        string? recordName = null,
        string[]? witnessUrls = null)
        => new(
            PanelRoles.Active,
            Name: name,
            Url: url,
            PublicIp: publicIp,
            PeerUrl: null,
            EnrollmentToken: null,
            DnsRecordName: recordName,
            SyncInterval: TimeSpan.FromHours(1),
            Grace: TimeSpan.FromHours(2),
            Cooldown: TimeSpan.Zero,
            WitnessUrls: witnessUrls ?? [],
            WitnessTimeout: TimeSpan.FromSeconds(2),
            StreamBatchSize: 5000);

    public static PanelOptions Standby(
        string name = "panel-b",
        string? url = "http://panel-b.test",
        string? peerUrl = "http://panel-a.test",
        string? token = null,
        string[]? witnessUrls = null)
        => Active(name, url, publicIp: "203.0.113.2", witnessUrls: witnessUrls) with
        {
            Role = PanelRoles.Standby,
            PeerUrl = peerUrl,
            EnrollmentToken = token
        };
}
