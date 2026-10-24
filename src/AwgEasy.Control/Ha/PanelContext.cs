namespace AwgEasy.Control;

/// <summary>
/// The live answer to "which role is this panel in", cached so the write guard does not read the
/// database on every request.
///
/// It is the role in the database and never the one in the environment. A promotion has to survive
/// a restart: an operator who promoted the standby at three in the morning must not find the
/// container has put itself back into standby because the compose file still says so.
/// </summary>
public sealed class PanelContext(PanelLocalStore store, ControlOptions options)
{
    private readonly Lock _gate = new();
    private PanelLocalState? _state;

    public PanelLocalState State
    {
        get
        {
            lock (_gate)
            {
                // Created on first read rather than at construction: the row cannot be written
                // before the schema exists, and this is resolved from the same container that
                // runs the migration.
                return _state ??= store.Ensure(options.Panel.Role, options.Panel.PeerUrl);
            }
        }
    }

    public bool IsActive => State.IsActive;

    public bool IsStandby => !State.IsActive;

    public string PanelId => State.PanelId;

    /// <summary>Re-reads after a promotion, a demotion or a sync. Cheap, and only called when
    /// something actually changed.</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _state = store.Find();
        }
    }
}
