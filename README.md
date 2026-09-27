# CUCM VT module

Run `vt cucm` with no subcommand to open the centralized home menu (users, phones, directory numbers, the local DID inventory, phone provisioning, and export) — the same menu is discoverable via `--help` and `--vt-module-describe` as this module's default command.

## Trusted CUCM certificate

Configure `trusted-certificate` with `vt module configure cucm` when CUCM AXL uses a CA certificate that is not available from system trust. Leaving the setting empty continues to use system trust. Absolute paths remain supported, and a standalone leading `~` expands to the home directory of the user running the module. The recommended per-user location is:

```text
~/.vt/modules/cucm/data/trusted-ca.pem
```

The setting does not perform shell expansion or environment-variable expansion. Named-user forms such as `~other/trusted-ca.pem` are rejected; use an absolute path when the certificate belongs outside the runtime user's home directory.

## User DN inventory

The module keeps an approved inventory of four-digit user directory numbers in its private VT data directory:

```text
~/.vt/modules/cucm/data/user-dids.json
```

The file uses the `vt-cucm-user-dids/v1` schema, is written atomically, and is readable only by the current user. It stores DN creation defaults and successful phone-slot assignments. It must contain user DNs only; room numbers are managed separately.

Configure the defaults applied to newly imported DIDs with `vt module configure cucm`:

- `user-did-partition`
- `user-did-css`
- `user-did-voicemail-profile`
- `user-did-description-prefix`
- `user-did-scan-partitions` (enables live CUCM reconciliation — see below)

Open `vt cucm dids` to browse, add, or replace inventory. The add and replace prompts accept comma-separated four-digit values or an equal-width numeric range:

```text
2100,2101
2100..2199
```

Use **Replace** to discard an unassigned inventory and seed a corrected list. Replacement is blocked when any local assignment exists and requires `Ctrl+Enter` or `Cmd+Enter` confirmation.

### Reconciling against live CUCM usage

The tracked inventory is always authoritative for *which numbers are valid DIDs* — CUCM is never trusted to expand that list on its own. It is, however, the source of truth for *whether a tracked number is currently in use*, since assignments can happen outside this tool (manual AXL/Admin changes) and some numbers are legitimately reserved by function rather than assigned to a person (call park, voicemail ports, translation patterns, hunt pilots, etc.).

Set `user-did-scan-partitions` (comma-separated CUCM route partition names, e.g. `AllPhones`) with `vt module configure cucm` to enable reconciliation. With it configured:

- `vt cucm dids list` merges the tracked inventory with every directory number actually configured in the scanned partition(s) and shows a STATUS/REASON for each:
  - `AVAILABLE` — not configured in CUCM; free to assign to a user or facility.
  - `RESERVED` — configured in CUCM, either assigned to a device or reserved by function (the reason names the CUCM `usage` value, e.g. `Call Park`, when it isn't a plain device line).
  - `ANOMALY` — needs attention: either a number configured in the scanned partition(s) that isn't in the tracked inventory at all (selecting its row offers **Reserve** — see below), or a tracked number this tool's local bookkeeping recorded as assigned but that CUCM no longer shows in the scanned partition(s) (stale; freed or reassigned outside this tool). Anomalies are never auto-corrected — review and fix the tracked inventory or CUCM configuration directly.
- `vt cucm dids available` lists only the tracked numbers currently `AVAILABLE`.
- Every DID assignment path (`users assign`, `phones` → **Assign available user DN**, `provision`) re-checks the selected DID against CUCM before saving and rejects it if CUCM now shows it in use or reserved by function, even if this tool's own bookkeeping still thought it was free.

Without `user-did-scan-partitions` configured, `dids list`/`available` and assignment fall back to the local inventory's recorded assignments only, exactly as before.

Numbers that show up as `ANOMALY` because they're plain internal extensions (not real DIDs) sharing the scanned partition — not misconfigurations — can be acknowledged with `vt cucm dids reserve` (or by selecting an individual anomaly row from `dids list`), which records them in a local `reserved-extensions.json` store so they stop being reported. This never makes a number assignable; it only silences numbers you've confirmed are something other than a DID. `vt cucm dids reserved` lists everything currently acknowledged this way.

A tracked DID can also show `ANOMALY` the other way: this tool's own bookkeeping thinks it's still assigned to a phone/user, but CUCM no longer shows it in the scanned partition(s) (the phone was reassigned, rebuilt, or the DN removed outside this tool). Selecting that row from `dids list` (or any DID's row, via `vt cucm dids detail <pattern> <partition>`) opens its detail screen; if it has a local assignment, a **Clear local assignment** action is offered there — review shows exactly which phone/line/user recorded it, and saving frees the DID for reassignment without touching CUCM itself (it only corrects the stale local record). `vt cucm dids clear-prompt` reaches the same review by typing the pattern and partition directly instead of navigating from `list`.

`vt cucm users list` maps each CUCM/LDAP `telephoneNumber` to an internal DN when the value contains exactly four digits. Select a user, choose **Assign phone and DN**, select a phone and a line slot, review the complete change, then press `Ctrl+Enter` or `Cmd+Enter` to save. The review screen's **OWNER ACTION** column shows whether the phone is getting a new owner, keeping its current one, or being reassigned away from another user. Saving adds the phone to the user's associated devices, sets the phone owner (replacing the previous owner if there was one — the previous owner's device association is removed automatically, best-effort), assigns the DN to the selected slot, ensures line 3 carries a room DN (leaving one alone if it already exists, otherwise assigning the `89898989` placeholder until a real per-phone/location room-DID source exists), and records the assignment locally.

The phone-first flow remains available by opening a phone and choosing **Assign user DN to slot**, which shows the phone's existing lines plus an **Add new line** row (next available index) and a **Custom** escape hatch for a specific index. Assigning a new line beyond what CUCM already has fails if the phone's button template has no free Line-type button position at that index — change the template first via **Edit** on the phone. Availability is tracked by the local inventory, while CUCM remains authoritative for DN objects and phone configuration.

For an existing four-digit CUCM DN, the module resolves and reuses the DN's actual route partition during review. If the DN does not exist, location-specific `user-did-*` defaults must be configured before the module will create it. The module never guesses a partition for a missing DN.

The versioned JSON document is a temporary persistence boundary. A future DirSync or connector implementation can replace the local repository while retaining the same user-DID and assignment fields.

## Directory numbers and phones

`vt cucm dn` lists directory numbers; selecting one opens **Info**, **Edit**, or **Delete**. Edit walks through calling search space, voicemail profile, call pickup group, and forward-all, then saves via review. Delete requires review confirmation. `vt cucm dn add` remains for creating a new DN directly, with the same live-selector chain (partition, CSS, voicemail profile, call pickup group, forward-all).

`vt cucm phones` lists phones — with an **&lt;Add phone&gt;** row at the end for creating a new, unregistered CUCM phone shell from scratch (name, description, product, device pool, phone button template, security profile, optional owner, then review/save); the same flow is available directly via `vt cucm phones add`. Selecting an existing phone opens **Edit** (description, device pool, owner, button template) alongside the existing line-label and DN-slot actions.

Each phone's **Numbers** listing (`phones` → select a phone → **Numbers**) shows every existing line plus an **Add new line** row (next available index). Selecting any line — existing or new — offers **Set directory number** (prompts for the DN, then its route partition, and works for a brand-new line the same way **Assign available user DN**/**Assign room DN** already did; it fails with button-template guidance if the line index has no free Line-type button position), **Assign available user DN**, **Assign room DN**, and **Set DN options** (see below). Once a line actually has a DN in CUCM, two more actions appear: **Set label** and **Set caller ID** (sets the line's outbound display name), plus **Remove directory number**.

## Set DN options and line templates

**Set DN options** (`phones` → select a phone → **Numbers** → select a line → **Set DN options**) is a per-field editor for everything on a line beyond the bare DN/partition: **Directory number & partition** (reuses **Set directory number**), **Alerting name**, **Caller ID (display)**, **Line text label**, **External phone number mask**, and **Owner (associated end user)**. Alerting name, caller ID, and external mask prompts preload the field's current CUCM value. Alerting name and external mask/owner-only edits are new dedicated single-field actions (`alerting-name`, `external-mask`, `dn-owner`); label and caller ID reuse the existing actions. The owner-only edit shows a review screen (Ctrl+Enter/Cmd+Enter to save) whose **ACTION** column states whether it's setting a new owner, keeping the current one, or replacing a different one — same convention as the users-assign flow.

Press **F1 (Apply template)** from Set DN options to fill every field in one step from a named preset instead of editing them one at a time. Configure presets with `vt module configure cucm`'s `line-templates` setting — a JSON object mapping template names to:

```json
{
  "HS-Classroom-Room": {
    "kind": "room",
    "alertingName": "Room {room}",
    "display": "HS Room {room}",
    "label": "Rm {room}"
  },
  "HS-Staff-User": {
    "kind": "user",
    "routePartitionName": "HS-Users",
    "alertingName": "{userDisplayName}",
    "display": "{userDisplayName}",
    "label": "{userDisplayName}",
    "externalPhoneNumberMask": "555XXXX",
    "associateEndUser": true
  }
}
```

- `kind: "room"` templates ignore `routePartitionName` and `associateEndUser` — the partition is always derived from the building resolved from the phone's **phone button template name prefix** (e.g. `HS` from `HS-UserRoom`) matched case-insensitively against the `building-patterns` setting's keys, and a room line never gets an owner. If the prefix can't be resolved, Apply Template falls back to a building selector before prompting for the room number.
- `kind: "user"` templates prompt for a DN pattern (typed manually — the local approved user-DN inventory isn't consulted here), and for a route partition too if the template doesn't supply one. When `associateEndUser` is `true`, it then prompts for an owner user ID (preloaded with the phone's current owner); leaving it blank applies no owner.
- Any string field may use tokens `{room}`, `{building}`, `{pattern}`, `{phoneName}`, `{lineIndex}`, `{devicePoolName}`, `{userDisplayName}`, `{userId}` (substituted from the resolved context; unresolved tokens for a `room` template, like `{userDisplayName}`, substitute to empty). A field omitted from the template is left unchanged in CUCM rather than blanked out.

After gathering context, Apply Template shows a Save-gated review of every resolved field (DN, partition, alerting name, caller ID, label, external mask, owner) before writing anything — creating the DN in CUCM if it doesn't already exist, assigning it to the line, and applying the remaining fields and (if applicable) the owner, with the same replace-with-confirmation behavior as other owner-setting flows.

## Phone descriptions and compliance

The module can auto-compose each phone's CUCM **Description** as a compliance summary instead of a plain free-text field. Configure `template-compliance-policies` with `vt module configure cucm` — a JSON object mapping phone button template names to the line slots that template is expected to have filled, and what kind of DN belongs in each:

```json
{
  "Standard 8861 SIP": {
    "slots": [
      { "index": 1, "kind": "user" },
      { "index": 3, "kind": "room" }
    ]
  }
}
```

- `"kind": "user"` slots must have any DN assigned.
- `"kind": "room"` slots must have a 3-digit room number, in the route partition its building expects (from `building-patterns`); the room slot also supplies the description's `RoomNumber` (building code + room number, e.g. `HS130`).
- `"kind": "speeddial"` is optional (at most one per template) and only consumed by the classroom workflow's All Call button (see above) — it plays no part in compliance/description evaluation.

`slots` also accepts a bracket-free `"index:kind,index:kind"` string (e.g. `"1:user,3:room"`) in place of the JSON array shown above — `vt module configure`'s value prompt crashes on a setting whose raw value contains literal `[`/`]` characters, so this form is required if you edit `template-compliance-policies` through `vt module configure` rather than hand-editing the YAML file directly.

Whenever a phone is saved — provisioned, edited, assigned/reassigned to a user, or has a line's DN added/changed/removed — the Description is recomposed:

- **Compliant**: `✔ | {RoomNumber} | {owner's CUCM display name, or fallback text}` (e.g. `✔ | HS130 | Victor Harris`).
- **Non-compliant** (template assigned, policy configured, but a slot check failed): `✘ | {RoomNumber} | {owner or fallback text}`.
- **Can't be evaluated** (no template assigned, no policy configured for the assigned template, the device pool isn't mapped to a building, or the policy's room slot has no valid room number): `? | {fallback text}`, unchanged from whatever the fallback text currently is.

The "fallback text" is manual, free-form text used whenever there's no owner (or the phone can't be evaluated) — edit it via the **Edit** phone action's **Description** prompt, which shows/accepts only that raw text (not the composed `✔/✘/? | ...` wrapper); leaving it blank keeps the current fallback text unchanged. A phone button template with no entry in `template-compliance-policies` is always treated as unevaluable (`?`).

## User DID defaults

Run `vt cucm configure`, choose **Defaults**, and edit any field — route partition, calling search space, voicemail profile, classroom forward-CSS, classroom CSS activation policy, no-answer ring duration, description prefix, scan partitions, and the classroom room/user line template names. Partition/CSS/voicemail-profile/forward-CSS use live CUCM selectors; the two classroom line template fields use a picker built from your actual `vt cucm templates` (filtered to the matching room/user kind) instead of typing a name from memory; CSS activation policy uses a picker built from `css-activation-policy-choices` when that's configured, otherwise a free-text prompt with a warning (see below). Saving any one field migrates every other field's current effective value (from the deprecated legacy settings, if this is the first edit) into the module-owned `module-defaults.json` store, which then takes precedence over those settings — the same "local store wins, legacy setting is a rollback source" pattern as `building-profiles.json`/`building-patterns`. The legacy `user-did-*` and `classroom-*-line-template` module settings are deprecated in favor of this command (their descriptions say so); they're still read as a fallback for any field never edited here.

There is no AXL operation to enumerate CUCM's "Calling Search Space Activation Policy" dropdown values (it's a fixed schema enum, not a queryable object like a route partition or calling search space) — configure `css-activation-policy-choices` (comma-separated, copied verbatim from Call Routing → Directory Number in CUCM Administration) to get a picker for it in **Defaults**; otherwise you'll type the value directly and must make sure it exactly matches your CUCM's dropdown text.

## Room DNs and building routing

Run `vt cucm configure`, choose **Buildings**, and then choose **Import legacy setting** once to migrate an existing `building-patterns` value into the local profile store. After migration, add or select a building from the same menu to manage it. The wizard uses live CUCM selectors for the room route partition, classroom target device pool, and phone button template, then prompts for any additional existing device pools that should resolve to the same building. Every add, edit, import, and delete ends with an explicit review and Save.

Building profiles are stored in the module data directory as `building-profiles.json`. Once this file exists it is authoritative; the `building-patterns` module setting remains unchanged as a legacy fallback and rollback source. Before migration, the module continues reading that setting exactly as earlier releases did.

The stored shape is:

```json
{
  "schema": "vt-cucm-building-profiles/v1",
  "buildings": {
    "PHS": {
      "routePartitionName": "HS-Rooms",
      "devicePoolName": "HighSchool",
      "devicePools": ["HighSchool", "HighSchool_SRST", "2024-HS-Pool"],
      "locationName": "PHS",
      "phoneTemplateName": "Standard 7841 SIP 1DN-1SdBLF-2DN",
      "roomExternalPhoneNumberMask": "2313482160",
      "allCallNumber": "2313480199"
    }
  }
}
```

The selected building's `devicePoolName`, `locationName`, and `phoneTemplateName` are assigned to the phone before its room line is configured. This keeps device-pool-controlled routing, media location, and button layout aligned with the room location. The `devicePools` list contains any additional existing pools that should resolve back to that building for audits; all mappings must remain disjoint. `roomExternalPhoneNumberMask` and `allCallNumber` are both optional and specific to this building (see the classroom workflow section above); the add/edit wizard prompts for each but accepts a blank value to leave it unconfigured.

The legacy `building-patterns` setting's `devicePools` also accepts a bracket-free comma-separated string (e.g. `"HighSchool,HighSchool_SRST"`) in place of a JSON array — `vt module configure`'s value prompt crashes on a setting whose raw value contains literal `[`/`]` characters, so this form is required if you edit `building-patterns` through `vt module configure` rather than hand-editing the YAML file directly. `building-profiles.json` (the migrated local store) isn't affected, since it's never rendered by that generic prompt.

From an existing phone, choose **Apply template** and then **Classroom**. Select a user and location, then enter the room's 3-digit number. The module resolves the effective phone button template and requires its `template-compliance-policies` entry to define exactly one user slot and one room slot. It also requires unambiguous `building-patterns` and complete room/user line templates named by the required `classroom-room-line-template` and `classroom-user-line-template` settings. The room template supplies shared external-mask and voicemail defaults, while the selected location always derives the room partition and the room description, alerting name, caller ID, and line label as `<LOCATION> Room <ROOM>` (for example, `PHS Room 130`). The configured user template supplies the user-line values and must set `associateEndUser` to `true`.

After the room number, a **scope** selection lets the two slots be applied independently: **Apply both** (the full classroom template — the original behavior), **Room number only** (assigns/updates just the room line; the user's DN, owner, and device association are left completely untouched), or **User/DN only** (assigns/updates just the user's DN, owner, and device association; the room line is left completely untouched). The review screen marks every field belonging to an excluded slot as `<Not applied>` / `Skip (excluded)` so it's clear nothing will be written for it, and a partial apply is not required to leave the phone fully compliant (unlike a full "both" apply, which still is) since it's an intentional incremental step. The device pool and button template are always brought in line regardless of scope, since either slot depends on the phone having the right button positions.

The room line's external phone number mask and an optional **All Call** BLF speed-dial hardkey are per-building, not per-template: configure them on each building profile through `vt cucm configure` → **Buildings**. Select the **All Call number** field directly, or proceed through add/edit, to open a searchable live CUCM directory-number selector with a `<None>` option; the stored destination therefore always names an existing number. The mask prompt accepts a blank value. If a building has no mask configured, the room line's external mask is simply left unmanaged (the review row shows `<Not configured for building>` with a note to configure it, rather than blocking or clearing the field). The user line's mask still comes from the `classroom-user-line-template` line template and is always required.

An **All Call** BLF speed dial (not a DN/line or keypad shortcut) can be added to the classroom template by declaring a `"speeddial"` slot in `template-compliance-policies` alongside the required `"user"`/`"room"` slots — at most one per template. The slot number is the physical button-template position; Save maps it to the first CUCM `busyLampFields` entry and sets the hardkey to the building's `allCallNumber` with the label "All Call". Reapplying the template also removes ordinary speed-dial entries with that destination, cleaning up entries created by versions 0.11.2 and 0.11.3. If the slot exists but the building has no number configured, the review row shows `<Not configured for building>` (configure it via **Buildings**, or Save without it — nothing is written for that button).

Press **F1 (Remove unspecified lines)** from the review screen to find and clear any line the phone has beyond the classroom template's user/room slots (for example, a stray line left over from a previous configuration) — it reviews every such line before removing it, unassigning the DN from the phone (the DN itself is not deleted from CUCM) and clearing any matching local user-DN inventory record.

Every user DN the classroom workflow assigns is also associated with that user directly on the directory number itself ("Users Associated with Line"), in addition to the phone's owner field — this happens automatically whenever the DN is created or updated and needs no configuration.

Optionally configure `user-did-forward-css` to have the classroom workflow manage a user DN's call-forward and call-pickup settings whenever it creates or updates that line: every forward variant (All, Busy, Busy Internal, No Answer, No Answer Internal, No Coverage, No Coverage Internal, On Failure, Not Registered, Not Registered Internal) uses that calling search space. The two No Answer variants forward to the user DN's voicemail profile; the other variants do not forward to voicemail. The DN's calling search space activation policy is set from `user-did-css-activation-policy` (default `Use System Default` — this and `css-activation-policy-choices` must match the literal dropdown text from CUCM's own Call Routing → Directory Number page on your system; there is no AXL operation to enumerate or verify these values), its call pickup group is always cleared to none, and — if `user-did-no-answer-ring-duration` is configured — both No Answer variants get that ring duration in seconds. Leaving `user-did-forward-css` unset leaves all of this entirely unmanaged. `vt cucm phones check <phone> classroom` verifies these same expectations (plus that the DN's voicemail profile actually exists in CUCM) against the live DN and reports any mismatch.

`vt cucm dn add`/`vt cucm dn edit` also let you assign a specific call pickup group to any directory number directly, picked live from CUCM (`<None>` clears it) — independent of the classroom workflow's always-clear-to-none behavior above.

The selected user's CUCM primary extension is authoritative when it is a four-digit DN; a four-digit LDAP telephone number is used only when no primary extension is assigned. The classroom workflow does not allocate from the local user-DN inventory when neither assigned value is available; that case must be handled through a dedicated allocation workflow.

Before writing anything, the module renders a deterministic review of every managed current and target value: device pool, location, button template, both DNs and partitions, DN creation defaults, labels, caller ID, external masks, voicemail, owner and device associations, compliance description, and local inventory assignment. The explicit **Save** row is bound to that reviewed state. Save re-reads CUCM and rejects the submission if the phone, user, room, or configured classroom policy changed after review. It then applies only required operations and, when anything changed, asks CUCM to apply the pending configuration to the physical phone with a soft device refresh. An already-converged retry performs no writes or refresh. If CUCM fails partway through, the module reports the failed operation, the underlying CUCM/AXL error message, and the operations already completed; because AXL offers no transaction across these resources, it requires a fresh review before retry instead of claiming rollback.

Every resolved `alertingName`, `display` (caller ID), and `label` value is validated against CUCM's own DeviceNumPlanMap constraint — 30 characters maximum and none of `[ ] " % < > & | { }` — before the review screen is even shown, so a template whose substituted text is too long (e.g. a fixed prefix combined with a long `{userDisplayName}`) or contains a disallowed character fails fast with a clear message instead of aborting mid-Save with a raw AXL fault.

From a phone's line menu (`vt cucm phones` → select a phone → **Numbers** → select a line), choose **Assign room DN** to select a building, enter a 3-digit room number, then review and save. The module creates the DN in the building's partition if it doesn't already exist (rejecting a room number that already exists in a *different* partition) and assigns it to the line — no local inventory entry is created, since room DNs are provisioned on demand rather than drawn from the approved user-DN pool.

The same line menu offers **Remove directory number**, which reviews the line's current pattern/partition, then on save removes the line's DN assignment from the phone in CUCM and clears any matching local user-DN inventory assignment so that DN becomes available again.

Run `vt cucm phones check <phone> classroom` to audit the applied classroom template from live CUCM data. It resolves the user and room slots from the phone button template's compliance policy, compares the user slot with the phone owner's primary extension (falling back to the owner's four-digit LDAP telephone number), resolves the building from the phone's device pool, and validates the room number and partition. When `user-did-forward-css` is configured it also verifies the user DN's call-forward settings, CSS activation policy, and call pickup group (see above), and it always verifies the user DN's voicemail profile exists in CUCM. The narrower `room-routing` profile performs only the building, room-number, and partition checks.

## Provisioning a phone from scratch

`vt cucm provision` (or **Provision a phone** from the root menu) chains phone creation/claiming and user assignment into one guided flow:

1. Choose **New phone** (walks through name, description, product, device pool, phone button template, security profile) or **Existing phone** (lists all CUCM phones, including already-owned ones, with their current owner shown).
2. Select a CUCM user with an available DN in the local inventory; users without one are listed but not selectable.
3. Review the phone, product, device pool, user, DN, and owner action, then save.

Saving creates (or claims) the phone with the user as owner, adds the phone to the user's associated devices, creates the DN in CUCM if needed, assigns it to line 1, ensures line 3 carries a room DN (same placeholder behavior as the users flow above), and records the assignment in the local inventory — reusing the same primitives as the DID and phone-assignment flows above. Claiming an already-owned existing phone replaces its owner and retains its other configuration (product, device pool, template, security profile) unchanged; the previous owner's device association is removed automatically, best-effort. If line assignment fails after the phone is created/claimed, the error names the phone so it can be finished manually via `vt cucm phones select <name>`.

The phone **Product** field stays free text (e.g. `"Cisco 8841"`) — unlike device pool/phone template/security profile, CUCM's AXL API has no `list`-style operation to enumerate valid phone models. The only theoretical route is a raw `executeSQLQuery` against undocumented internal database tables, which Cisco's own AXL developer guide explicitly advises against relying on ("the underlying CUCM database structure may change without notice in any CUCM release"), so this module doesn't attempt it.

## Exporting phones for analysis

`vt cucm get phones` (or **Export CUCM data** from the root menu) pulls phones, optionally filtered to one device pool, with a progress bar while paging, then writes them to the console or a CSV file. Each row includes the phone's identity/config fields plus a `LINES` column (`index:pattern@partition`, pipe-separated) so a downstream tool can analyze existing partition/CSS/voicemail-profile patterns per device pool for a bulk rollout. Per-line calling search space and voicemail profile are not included — cross-reference `vt cucm dn` for those, since fetching them per line here would require one extra AXL call per line.

## Internal architecture notes

- **`CucmConfiguration`** ([CucmConfiguration.cs](CucmConfiguration.cs)) is a typed, VTModuleSDK-independent aggregate of every module setting (built from a plain `IReadOnlyDictionary<string, string>` via `FromModuleConfiguration`). It delegates to the existing `PhoneConfigurationChecks.Parse*`/`UserDidReconciler.ParsePartitions` methods, which remain available on their own as "compatibility readers" for callers that only need one setting. Command handlers are not required to switch to it yet — it exists for callers that need several settings at once, and as groundwork for a future structured-configuration protocol.
- **`CucmResourceQueryService`** ([CucmResourceQueryService.cs](CucmResourceQueryService.cs)) wraps `CucmService`'s live AXL named-resource lookups (route partitions, device pools, phone button templates, voicemail profiles, calling search spaces, security profiles) with the filtering (blank names) and sorting every selector menu needs, so menu code doesn't duplicate that logic per call site. `vt cucm configure` and `vt cucm templates` use it for their live selectors today.