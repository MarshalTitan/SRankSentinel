using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace SRankSentinel;

public sealed partial class Plugin
{
    private readonly IPlayerState huntJobPlayer;
    private readonly HuntJobReadiness huntJob = new();
    private uint lastHuntJob;
    private string? jobPause;
    private DateTime jobPausedSince;
    private ulong GearsetOwner => huntJobPlayer is { IsLoaded: true } ? huntJobPlayer.ContentId : 0;

    private HuntGearset? ChosenSet(ulong owner) => config.HuntingGearsets.GetValueOrDefault(owner);

    private static unsafe HuntGearset? ReadHuntGearset(int slot)
    {
        var m = RaptureGearsetModule.Instance();
        if (m == null || slot < 0 || slot >= m->Entries.Length || !m->IsValidGearset(slot)) return null;
        var e = m->GetGearset(slot);
        if (e == null || !e->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists) ||
            e->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.MainHandMissing) ||
            CombatController.GetBaseRangedTagAction(e->ClassJob) == 0) return null;
        return new(slot, e->ClassJob, e->NameString);
    }

    private void DrawHuntGearset()
    {
        var owner = GearsetOwner;
        if (owner == 0 || travel.IsBusy) return;
        var choice = ChosenSet(owner);
        if (ImGui.BeginCombo("Combat gearset", choice is null ? "None (manual job change)" : $"#{choice.Slot + 1} {choice.Name}"))
        {
            try
            {
                if (ImGui.Selectable("None (manual job change)", choice is null)) Select(null);
                for (var slot = 0; slot < 100; slot++)
                    if (ReadHuntGearset(slot) is { } set &&
                        ImGui.Selectable($"#{slot + 1} {set.Name}###huntgear{slot}", set == choice)) Select(set);
            }
            finally { ImGui.EndCombo(); }
        }
        if (jobPause != null && huntJob.Attempts >= HuntJobReadiness.Limit &&
            ImGui.Button("Retry combat gearset")) huntJob.Retry();

        void Select(HuntGearset? set)
        {
            if (set == null) config.HuntingGearsets.Remove(owner);
            else config.HuntingGearsets[owner] = set;
            huntJob.Retry();
            config.Save();
        }
    }

    private string? GearsetBlock()
    {
        var reason = HuntJobReadiness.Restriction(travel.IsBusy || !CanActAfterRevival(), condition[ConditionFlag.InCombat],
            condition.Any(ConditionFlag.Fishing, ConditionFlag.Gathering, ConditionFlag.ExecutingGatheringAction),
            condition.Any(ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56, ConditionFlag.BoundByDuty95),
            objects.LocalPlayer is { IsCasting: false } && condition[ConditionFlag.NormalConditions] &&
            condition.OnlyAny(ConditionFlag.NormalConditions));
        return reason ?? (lifestream.ReadReadiness() == LifestreamReadiness.Idle ? null : "World travel is busy or unavailable");
    }

    private bool EnsureHuntJob(DateTime now)
    {
        if (resetToUldahAllowsLiveEntityExit) return true; // explicit manual reset remains available
        try { return CheckLiveHuntJob(now); }
        catch (Exception ex)
        {
            const string error = "Hunting paused: unable to verify the combat job or gearset.";
            if (jobPause != error)
            {
                if (jobPause == null) jobPausedSince = now;
                fullAssist.End("Combat job unavailable");
                vnav.StopOwnedMovementSafe("combat job unavailable");
                log.Warning(ex, "{Status} state={State}", error, state);
            }
            status = jobPause = error;
            return false;
        }
    }

    private unsafe bool CheckLiveHuntJob(DateTime now)
    {
        var player = objects.LocalPlayer;
        var job = player?.ClassJob.RowId ?? 0;
        var owner = GearsetOwner;
        var selected = ChosenSet(owner);
        var supported = CombatController.GetBaseRangedTagAction(job) != 0;
        if (job != 0 && lastHuntJob != job)
        {
            if (lastHuntJob != 0)
            {
                log.Information("Hunt job changed: {OldJob} -> {Job}; selected gearset={Set}, state={State}", lastHuntJob, job, selected?.ToString() ?? "none", state);
                activeTagActionId = 0;
                ResetPendingTagDispatch(); // never confirm an old job's submission with the new job's sequence
            }
            lastHuntJob = job;
        }
        var eligible = supported && (selected == null || selected.Job == job);
        string? blocked = null;
        bool valid = false;
        int slot = -1;
        // In ordinary play this gate reads only the live job; native gearset calls are recovery-only.
        if (owner != 0 && job != 0 && selected != null && (!eligible || huntJob.Attempts > 0))
        {
            blocked = GearsetBlock();
            var module = travel.IsBusy ? null : RaptureGearsetModule.Instance();
            if (module != null)
            {
                slot = module->CurrentGearsetIndex;
                valid = ReadHuntGearset(selected.Slot) == selected;
            }
        }
        var step = huntJob.Check(now, owner, job, supported, selected, slot, valid, blocked == null);
        if (step == JobStep.Ready)
        {
            if (jobPause != null)
            {
                log.Information("Hunt job ready: job={Job}, expected gearset={Set}, current slot={Slot}; resuming {State}", job, selected?.ToString() ?? "none", slot >= 0 ? (slot + 1).ToString() : "not needed", state);
                // Time spent paused is not a failure of the original route or tag. Never extend SS/freshness deadlines.
                if (stateSinceUtc < jobPausedSince) stateSinceUtc += now - jobPausedSince;
                if (tagRequired) tagRequiredSinceUtc = tagLastProgressUtc = now;
                status = "Combat job ready; resuming hunt";
                jobPause = null;
            }
            return true;
        }
        var detected = player?.ClassJob.ValueNullable?.Name.ToString();
        if (string.IsNullOrWhiteSpace(detected)) detected = $"job {job}";
        string reason = step switch
        {
            JobStep.Loading => "waiting for the local character and job",
            JobStep.Select => "select a combat gearset or equip a supported combat job",
            JobStep.Invalid => "selected combat gearset unavailable; check its slot, name, job and main hand",
            JobStep.Blocked => $"unable to restore combat gearset while {blocked}",
            JobStep.Failed => $"unable to restore combat gearset after {huntJob.Attempts} attempts; equip it manually or use Retry",
            JobStep.Equip => $"restoring combat gearset #{selected!.Slot + 1} (attempt {huntJob.Attempts}/{HuntJobReadiness.Limit})",
            _ => "waiting for the game to confirm the combat gearset",
        };
        var message = $"Hunting paused: {detected} equipped; {reason}.";
        if (jobPause == null)
        {
            jobPausedSince = now;
            fullAssist.End("Combat job not ready");
            vnav.StopOwnedMovementSafe("combat job not ready");
            if (state == SentinelState.CheckNormalSPresence) normalSPresence.PauseObservation();
        }
        if (message != jobPause)
            log.Information("Job readiness: {Message} job={Job}, expected gearset={Set}, state={State}", message, job, selected?.ToString() ?? "none", state);
        status = jobPause = message;
        if (step == JobStep.Equip)
        {
            // Recheck identity, restrictions and selection at the actual write; never cancel another activity.
            var m = RaptureGearsetModule.Instance();
            if (m != null && owner == GearsetOwner && selected == ChosenSet(owner) &&
                GearsetBlock() == null && ReadHuntGearset(selected!.Slot) == selected)
            {
                var result = m->EquipGearset(selected.Slot);
                log.Information("Combat gearset #{Slot} requested: job={Job}, expected={Expected}, attempt={Attempt}, native result={Result}; awaiting live confirmation",
                    selected.Slot + 1, job, selected.Job, huntJob.Attempts, result);
            }
        }
        return false;
    }
}
