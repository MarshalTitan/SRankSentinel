using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace SRankSentinel;

// Public WrathCombo 1.0.4.27 IPC. Changes below are lease overrides, never saved settings.
internal sealed class CombatAssistIpc(IDalamudPluginInterface pi, Action<string>? report = null) : ICombatAssistProvider, IDisposable
{
    private const string Wrath = "WrathCombo.";
    private const string Prefix = "SRankSentinel";
    private Lease? owned;
    private DateTime retryAfter;
    private readonly RotationSolverRebornIpc reborn = new(pi);
    // WrathCombo.API.AutoRotationConfigOption: InCombatOnly, DPSRotationMode=Manual,
    // OnlyAttackInCombat, BypassQuest, BypassFATE. The setter accepts boxed values.
    private static readonly (int Option, object Value)[] Options = [(0, true), (1, 0), (13, true), (21, false), (22, false)];

    private bool RotationOn() => pi.GetIpcSubscriber<bool>(Wrath + "GetAutoRotationState").InvokeFunc();

    internal string? RotationUnavailable()
    {
        try
        {
            if (owned is { Closing: true }) return "Waiting for Wrath lease release";
            if (!pi.GetIpcSubscriber<bool>(Wrath + "IPCReady").InvokeFunc()) return "Wrath IPC unavailable";
            if (!pi.GetIpcSubscriber<bool>(Wrath + "IsCurrentJobAutoRotationReady").InvokeFunc())
                return "Wrath Auto-Rotation is not ready for this job";
            if (owned is null && RotationOn())
                return "Turn standalone Wrath Auto-Rotation off so Sentinel can start and stop it";
            return reborn.Conflict();
        }
        catch { return "Wrath readiness IPC unavailable"; }
    }

    public string? Unavailable(AssistFight fight) =>
        RotationUnavailable() ?? BossModCombatReadiness.Unavailable(pi, fight.BaseId);

    public IGuardedRotation? TryAcquire(AssistFight fight)
    {
        if (owned is not null || Unavailable(fight) is not null) return null;
        var lease = new Lease(this);
        owned = lease;
        try
        {
            if (!pi.GetIpcSubscriber<Guid, object>(Wrath + "ReleaseControl").HasAction)
                throw new InvalidOperationException("ReleaseControl unavailable");
            // Unique callback per grant; a late cancellation cannot revoke a later hunt's lease.
            var prefix = Prefix + "." + Guid.NewGuid().ToString("N");
            lease.Callback = pi.GetIpcProvider<int, string, object>(prefix + ".WrathComboCallback");
            lease.Callback.RegisterAction((reason, _) => { lease.Revoked = true; });
            lease.Id = pi.GetIpcSubscriber<string, string, string, Guid?>(Wrath + "RegisterForLeaseWithCallback")
                .InvokeFunc(Prefix, "S Rank Sentinel", prefix);
            if (lease.Id is not Guid id || id == Guid.Empty) throw new InvalidOperationException("Wrath denied lease");
            // Wrath 1.0.4.27 reads its per-lease rotation slot if config overrides already exist.
            // Seed that slot OFF first, while standalone rotation is off; enable only at the end.
            if (lease.Revoked || RotationOn() ||
                pi.GetIpcSubscriber<Guid, bool, int>(Wrath + "SetAutoRotationState").InvokeFunc(id, false) is not (0 or 13))
                throw new InvalidOperationException("Wrath did not grant an inactive rotation lease");
            foreach (var (option, value) in Options)
            {
                if (lease.Revoked || pi.GetIpcSubscriber<Guid, object, object, int>(Wrath + "SetAutoRotationConfigState")
                    .InvokeFunc(id, option, value) is not (0 or 13))
                    throw new InvalidOperationException($"Wrath denied option {option}");
            }
            if (!OptionsMatch() || lease.Revoked || RotationOn() || Unavailable(fight) is not null)
                throw new InvalidOperationException("Rotation settings or ownership changed before enable");
            if (pi.GetIpcSubscriber<Guid, bool, int>(Wrath + "SetAutoRotationState").InvokeFunc(id, true) is not (0 or 13) ||
                !lease.Healthy) throw new InvalidOperationException("Wrath did not enable rotation");
            return lease;
        }
        catch (Exception ex)
        {
            report?.Invoke("Wrath handoff declined: " + ex.Message);
            lease.Dispose();
            return null;
        }
    }

    private bool OptionsMatch()
    {
        var read = pi.GetIpcSubscriber<object, object?>(Wrath + "GetAutoRotationConfigState");
        foreach (var (option, expected) in Options)
        {
            var observed = read.InvokeFunc(option);
            if (!Equals(expected, observed)) return false;
        }
        return true;
    }

    internal void RetryRelease(DateTime now)
    {
        if (owned is { Closing: true } lease && now >= retryAfter) Close(lease);
    }

    private void Close(Lease lease)
    {
        if (owned != lease) return;
        lease.Closing = true;
        try
        {
            if (lease.Id is Guid id && !lease.Revoked)
                pi.GetIpcSubscriber<Guid, object>(Wrath + "ReleaseControl").InvokeAction(id);
        }
        catch
        {
            // Best effort if ReleaseControl alone failed: stop our leased rotation while retrying.
            if (lease.Id is Guid id && !lease.Revoked)
                try { pi.GetIpcSubscriber<Guid, bool, int>(Wrath + "SetAutoRotationState").InvokeFunc(id, false); }
                catch { /* Wrath may have unloaded entirely. Keep release pending. */ }
            if (retryAfter == default) report?.Invoke("Wrath release unavailable; retrying before any new grant");
            retryAfter = DateTime.UtcNow.AddSeconds(1);
            return;
        }
        lease.RemoveCallback();
        owned = null;
        retryAfter = default;
    }

    public void Dispose()
    {
        if (owned is not { } lease) return;
        Close(lease);
        if (owned is not null) lease.RemoveCallback();
    }

    private sealed class Lease(CombatAssistIpc owner) : IGuardedRotation
    {
        internal Guid? Id;
        internal ICallGateProvider<int, string, object>? Callback;
        internal volatile bool Revoked;
        internal bool Closing;
        internal void RemoveCallback()
        {
            try { Callback?.UnregisterAction(); }
            catch (Exception ex) { owner.WarnCallback(ex); }
            Callback = null;
        }
        public bool Healthy => !Revoked && !Closing && owner.RotationOn() && owner.OptionsMatch();
        public void Dispose() => owner.Close(this);
    }

    private void WarnCallback(Exception ex) => report?.Invoke("Wrath callback cleanup failed: " + ex.Message);
}
