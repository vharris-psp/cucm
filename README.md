# CUCM VT module

Run `vt cucm` with no subcommand to open the centralized home menu (users, phones, directory numbers, the local DID inventory, phone provisioning, and export) — the same menu is discoverable via `--help` and `--vt-module-describe` as this module's default command.

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

Open `vt cucm dids` to browse, add, or replace inventory. The add and replace prompts accept comma-separated four-digit values or an equal-width numeric range:

```text
2100,2101
2100..2199
```

Use **Replace** to discard an unassigned inventory and seed a corrected list. Replacement is blocked when any local assignment exists and requires `Ctrl+Enter` or `Cmd+Enter` confirmation.

`vt cucm users list` maps each CUCM/LDAP `telephoneNumber` to an internal DN when the value contains exactly four digits. Select a user, choose **Assign phone and DN**, select a phone and a line slot, review the complete change, then press `Ctrl+Enter` or `Cmd+Enter` to save. The review screen's **OWNER ACTION** column shows whether the phone is getting a new owner, keeping its current one, or being reassigned away from another user. Saving adds the phone to the user's associated devices, sets the phone owner (replacing the previous owner if there was one — the previous owner's device association is removed automatically, best-effort), assigns the DN to the selected slot, ensures line 3 carries a room DN (leaving one alone if it already exists, otherwise assigning the `89898989` placeholder until a real per-phone/location room-DID source exists), and records the assignment locally.

The phone-first flow remains available by opening a phone and choosing **Assign user DN to slot**, which shows the phone's existing lines plus an **Add new line** row (next available index) and a **Custom** escape hatch for a specific index. Assigning a new line beyond what CUCM already has fails if the phone's button template has no free Line-type button position at that index — change the template first via **Edit** on the phone. Availability is tracked by the local inventory, while CUCM remains authoritative for DN objects and phone configuration.

For an existing four-digit CUCM DN, the module resolves and reuses the DN's actual route partition during review. If the DN does not exist, location-specific `user-did-*` defaults must be configured before the module will create it. The module never guesses a partition for a missing DN.

The versioned JSON document is a temporary persistence boundary. A future DirSync or connector implementation can replace the local repository while retaining the same user-DID and assignment fields.

## Directory numbers and phones

`vt cucm dn` lists directory numbers; selecting one opens **Info**, **Edit**, or **Delete**. Edit walks through calling search space, voicemail profile, and forward-all, then saves via review. Delete requires review confirmation. `vt cucm dn add` remains for creating a new DN directly.

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

Whenever a phone is saved — provisioned, edited, assigned/reassigned to a user, or has a line's DN added/changed/removed — the Description is recomposed:

- **Compliant**: `✔ | {RoomNumber} | {owner's CUCM display name, or fallback text}` (e.g. `✔ | HS130 | Victor Harris`).
- **Non-compliant** (template assigned, policy configured, but a slot check failed): `✘ | {RoomNumber} | {owner or fallback text}`.
- **Can't be evaluated** (no template assigned, no policy configured for the assigned template, the device pool isn't mapped to a building, or the policy's room slot has no valid room number): `? | {fallback text}`, unchanged from whatever the fallback text currently is.

The "fallback text" is manual, free-form text used whenever there's no owner (or the phone can't be evaluated) — edit it via the **Edit** phone action's **Description** prompt, which shows/accepts only that raw text (not the composed `✔/✘/? | ...` wrapper); leaving it blank keeps the current fallback text unchanged. A phone button template with no entry in `template-compliance-policies` is always treated as unevaluable (`?`).

## Room DNs and building routing

Configure `building-patterns` with `vt module configure cucm` — a JSON object mapping building codes to the route partition and device pools used for local room routing:

```json
{
  "PHS": { "routePartitionName": "PHS-Rooms", "devicePools": ["PHS-DP-Classrooms", "PHS-DP-Office"] },
  "MS":  { "routePartitionName": "MS-Rooms",  "devicePools": ["MS-DP-Classrooms"] }
}
```

Each building's `devicePools` list should be disjoint — a device pool listed under two buildings is flagged as a configuration error by the room-routing check below, since it makes the building unresolvable from the phone alone.

From a phone's line menu (`vt cucm phones` → select a phone → **Numbers** → select a line), choose **Assign room DN** to select a building, enter a 3-digit room number, then review and save. The module creates the DN in the building's partition if it doesn't already exist (rejecting a room number that already exists in a *different* partition) and assigns it to the line — no local inventory entry is created, since room DNs are provisioned on demand rather than drawn from the approved user-DN pool.

The same line menu offers **Remove directory number**, which reviews the line's current pattern/partition, then on save removes the line's DN assignment from the phone in CUCM and clears any matching local user-DN inventory assignment so that DN becomes available again.

Run `vt cucm phones check <phone> room-routing` to audit a phone: it resolves the phone's building from its device pool (via `building-patterns`), flags an unresolved or ambiguous device pool, and checks that line 3 has a 3-digit number in the building's expected partition. This complements the existing `classroom` profile, which validates line 1/line 3 against the temporary `phone-check-placeholder` assignment source.

## Provisioning a phone from scratch

`vt cucm provision` (or **Provision a phone** from the root menu) chains phone creation/claiming and user assignment into one guided flow:

1. Choose **New phone** (walks through name, description, product, device pool, phone button template, security profile) or **Existing phone** (lists all CUCM phones, including already-owned ones, with their current owner shown).
2. Select a CUCM user with an available DN in the local inventory; users without one are listed but not selectable.
3. Review the phone, product, device pool, user, DN, and owner action, then save.

Saving creates (or claims) the phone with the user as owner, adds the phone to the user's associated devices, creates the DN in CUCM if needed, assigns it to line 1, ensures line 3 carries a room DN (same placeholder behavior as the users flow above), and records the assignment in the local inventory — reusing the same primitives as the DID and phone-assignment flows above. Claiming an already-owned existing phone replaces its owner and retains its other configuration (product, device pool, template, security profile) unchanged; the previous owner's device association is removed automatically, best-effort. If line assignment fails after the phone is created/claimed, the error names the phone so it can be finished manually via `vt cucm phones select <name>`.

## Exporting phones for analysis

`vt cucm get phones` (or **Export CUCM data** from the root menu) pulls phones, optionally filtered to one device pool, with a progress bar while paging, then writes them to the console or a CSV file. Each row includes the phone's identity/config fields plus a `LINES` column (`index:pattern@partition`, pipe-separated) so a downstream tool can analyze existing partition/CSS/voicemail-profile patterns per device pool for a bulk rollout. Per-line calling search space and voicemail profile are not included — cross-reference `vt cucm dn` for those, since fetching them per line here would require one extra AXL call per line.