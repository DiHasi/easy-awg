using System.Globalization;

namespace AwgEasy.Control;

public enum PanelFailoverAction
{
    /// <summary>In touch with the panel in charge. Nothing to do.</summary>
    None,

    /// <summary>Out of touch, but not for long enough yet.</summary>
    Wait,

    /// <summary>Long enough, but this panel promoted or stepped down too recently.</summary>
    Hold,

    /// <summary>Long enough, and automatic promotion is off: the operator is told, nothing moves.</summary>
    Recommend,

    /// <summary>Long enough and armed, but something forbids it - and the reason says what.</summary>
    Refused,

    /// <summary>Take over.</summary>
    Promote
}

public sealed record PanelFailoverDecision(PanelFailoverAction Action, string Message);

/// <summary>
/// Whether this standby should take over. Pure, like <see cref="FailoverPolicy"/>, so the whole
/// judgement can be tested without a panel, a peer or a network.
///
/// It is more reluctant than node failover, and for a reason worth stating plainly: a node has an
/// independent witness and a panel does not. When probes in three networks all fail to reach a
/// node, the node is the problem. When a standby cannot reach the active, the standby has learned
/// exactly one thing - that it cannot reach the active - and it is in no position to say which of
/// the two of them is the one that is cut off. Promoting on that evidence alone is how a network
/// split becomes two panels both allocating addresses out of the same subnet.
///
/// So three things stand between an armed standby and a promotion. It must have failed to pull
/// for the whole grace period, which is many polls and not one. It must already hold fleet state,
/// because a panel promoted without any would publish an empty fleet and every node would
/// dutifully converge onto it and drop every peer it serves. And its own uplink must still be
/// working, checked against something outside the pair - which is the nearest thing to a witness
/// there is, and is what separates "the active is gone" from "my network broke".
///
/// Even then it is not safety, only a sharply reduced chance of being wrong. The term is what
/// makes being wrong survivable: whichever panel promoted last wins, deterministically, and the
/// other one steps down the moment the two can see each other again.
/// </summary>
public static class PanelFailoverPolicy
{
    /// <param name="mode">The fleet's panel failover mode, as of the last snapshot. Read from the
    /// replicated state on purpose: a standby that has lost contact still knows what it was told
    /// to do, which is the only moment the setting matters.</param>
    /// <param name="revoked">Whether the panel in charge has cut this one off. A revoked standby
    /// is one an operator deliberately took out of the pair, and it must not promote itself back
    /// in on the strength of no longer being able to reach the panel that revoked it.</param>
    /// <param name="witnessConfigured">False means there is nothing outside the pair to ask.</param>
    /// <param name="witnessOnline">Null while it has not been checked this round.</param>
    public static PanelFailoverDecision Decide(
        DateTimeOffset now,
        string mode,
        bool held,
        bool revoked,
        PanelLocalState state,
        bool witnessConfigured,
        bool? witnessOnline,
        PanelOptions options)
    {
        if (!state.IsEnrolled)
        {
            return new PanelFailoverDecision(
                PanelFailoverAction.Refused,
                "This panel has not been enrolled into a pair yet, so it has no fleet to take over. "
                + "Issue a standby token on the panel in charge and point this one at it.");
        }

        // The clock is this panel's own, stamped when a pull succeeded. Deliberately not the
        // peer's: whether contact has been lost is judged here, and a peer whose clock runs ahead
        // must not be able to hold a standby back from noticing it has gone.
        if (state.SyncedAt is { } syncedAt && now - syncedAt <= options.Grace)
        {
            return new PanelFailoverDecision(
                PanelFailoverAction.None,
                $"In touch with the panel in charge, last {Describe(now - syncedAt)} ago.");
        }

        var silence = state.SyncedAt is { } last
            ? $"for {Describe(now - last)}"
            : "since this panel started";

        if (state.SyncedAt is null && !state.HasFleetState)
        {
            return new PanelFailoverDecision(
                PanelFailoverAction.Refused,
                "This panel has never completed a sync, so it holds no fleet state. Promoting it would "
                + "publish an empty fleet and every node would drop every peer it serves. Get it syncing first.");
        }

        if (mode != PanelFailoverModes.Automatic)
        {
            return new PanelFailoverDecision(
                PanelFailoverAction.Recommend,
                $"No contact with the panel in charge {silence}. Automatic promotion is off, so nothing has "
                + "moved: promote this panel by hand if the other one is really gone.");
        }

        if (held)
        {
            return new PanelFailoverDecision(
                PanelFailoverAction.Refused,
                $"No contact with the panel in charge {silence}, and this panel is on hold. "
                + "Release the hold to let it take over, or promote it by hand.");
        }

        if (revoked)
        {
            return new PanelFailoverDecision(
                PanelFailoverAction.Refused,
                $"No contact with the panel in charge {silence}, and this panel is revoked - it was taken out "
                + "of the pair deliberately. Losing contact with the panel that revoked it is not a reason to come back.");
        }

        if (!state.HasFleetState)
        {
            return new PanelFailoverDecision(
                PanelFailoverAction.Refused,
                "This panel holds no fleet state, so taking over would publish an empty fleet to every node.");
        }

        if (!witnessConfigured)
        {
            return new PanelFailoverDecision(
                PanelFailoverAction.Refused,
                $"No contact with the panel in charge {silence}, but there is nothing outside the pair to check "
                + "against: with AWG_PANEL_WITNESS_URLS switched off, this panel cannot tell the other one being "
                + "gone from its own uplink being gone. Promote it by hand.");
        }

        if (witnessOnline != true)
        {
            return new PanelFailoverDecision(
                PanelFailoverAction.Refused,
                $"No contact with the panel in charge {silence}, and this panel cannot reach anything else either. "
                + "The network it sits in is the likelier problem, so it is not taking over.");
        }

        // The same reluctance node failover has, for the same reason: without it a pair that is
        // half-broken in both directions would hand the fleet back and forth every grace period.
        if (state.PromotedAt is { } promotedAt && now - promotedAt < options.Cooldown)
        {
            return new PanelFailoverDecision(
                PanelFailoverAction.Hold,
                $"No contact with the panel in charge {silence}, but this panel changed role "
                + $"{Describe(now - promotedAt)} ago. Waiting out the {Describe(options.Cooldown)} cooldown.");
        }

        return new PanelFailoverDecision(
            PanelFailoverAction.Promote,
            $"No contact with the panel in charge {silence}, this panel still has a working uplink, and it holds "
            + "the fleet state. Taking over.");
    }

    /// <summary>The same wording the node policy uses, so two cards do not describe one duration
    /// in two different ways.</summary>
    public static string Describe(TimeSpan span)
        => span.TotalSeconds < 120
            ? $"{Math.Round(span.TotalSeconds)} s"
            : span.TotalMinutes < 120
                ? $"{Math.Round(span.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min"
                : $"{Math.Round(span.TotalHours).ToString(CultureInfo.InvariantCulture)} h";
}
