# CUCM VT module

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

`vt cucm users list` maps each CUCM/LDAP `telephoneNumber` to an internal DN when the value contains exactly four digits. Select a user, choose **Assign phone and DN**, select an unowned phone and a line slot, review the complete change, then press `Ctrl+Enter` or `Cmd+Enter` to save. The module adds the phone to the user's associated devices, sets the phone owner, assigns the DN to the selected slot, and records the assignment locally.

The phone-first flow remains available by opening a phone and choosing **Assign user DN to slot**. Availability is tracked by the local inventory, while CUCM remains authoritative for DN objects and phone configuration.

For an existing four-digit CUCM DN, the module resolves and reuses the DN's actual route partition during review. If the DN does not exist, location-specific `user-did-*` defaults must be configured before the module will create it. The module never guesses a partition for a missing DN.

The versioned JSON document is a temporary persistence boundary. A future DirSync or connector implementation can replace the local repository while retaining the same user-DID and assignment fields.

## Directory numbers and phones

`vt cucm dn` lists directory numbers; selecting one opens **Info**, **Edit**, or **Delete**. Edit walks through calling search space, voicemail profile, and forward-all, then saves via review. Delete requires review confirmation. `vt cucm dn add` remains for creating a new DN directly.

`vt cucm phones` lists phones; selecting one opens **Edit** (description, device pool, owner) alongside the existing line-label and DN-slot actions. `vt cucm phones add` walks through name, description, product, device pool, phone button template, and security profile before review.

## Provisioning a phone from scratch

`vt cucm provision` (or **Provision a phone** from the root menu) chains phone creation/claiming and user assignment into one guided flow:

1. Choose **New phone** (walks through name, description, product, device pool, phone button template, security profile) or **Existing phone** (lists unowned CUCM phones).
2. Select a CUCM user with an available DN in the local inventory; users without one are listed but not selectable.
3. Review the phone, product, device pool, user, and DN, then save.

Saving creates (or claims) the phone with the user as owner, adds the phone to the user's associated devices, creates the DN in CUCM if needed, assigns it to line 1, and records the assignment in the local inventory — reusing the same primitives as the DID and phone-assignment flows above. If line assignment fails after the phone is created/claimed, the error names the phone so it can be finished manually via `vt cucm phones select <name>`.