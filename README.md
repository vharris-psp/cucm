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

`vt cucm phones` lists phones; selecting one opens **Edit** (description, device pool, owner, button template) alongside the existing line-label and DN-slot actions. `vt cucm phones add` walks through name, description, product, device pool, phone button template, and security profile before review.

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