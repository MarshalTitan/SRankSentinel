# Navigation polish - 0.7.78.0

Uses compatible SentinelCore UI 0.4.1.2, source `ffa1a6ed3c89337bfb901b566b9339e2e229d154`. Existing Modern sidebar icons have borderless idle states, persistent rounded selection, soft animated hover highlights and padded translucent tooltip bubbles. Custom minimize/restore controls use drawn down/up chevrons; close uses a more visible X. Native hit areas, keyboard/controller focus, window dragging, saved placement and callbacks remain intact. Headers follow the selected page. Classic and plugin functionality are preserved.

Main Expansion Hunting, Data Center Hunting and Tagging sections, both distance profile cards, Normal Tagging and Full Combat Assist groups, and Spawn Reports remember disclosure state. Existing installs default to expanded. Recovery is never collapsible and remains outside the scrolling settings region. Disclosures save presentation only. History retains its two colours, seven-day grouping, metrics, totals and hover data, with modest outer bar rounding and flat stack seams.

Normal requirements are vnavmesh, Lifestream and at least one enabled HuntAlerts/Sonar source. The full mode reuses those four controls plus BossMod AI and one selected rotation provider. Readiness uses existing read-only IPC signals; no lease is acquired by rendering. The existing RSR handoff remains NOT READY because its IPC cannot verify job readiness and exclusive control; this UI change does not change that integration.

Dependency packages are restored from the immutable release and SHA-256 checked before locked restore. Locks record the release SHA-512 values. Automated builds/native UI/persistence checks are recorded in the coordinated Sentinel report. Actual FFXIV visual acceptance remains separate from native renderer checks.
