# CUCM VT module

## User DID inventory

The module keeps an approved inventory of user DIDs in its private VT data directory:

```text
~/.vt/modules/cucm/data/user-dids.json
```

The file uses the `vt-cucm-user-dids/v1` schema, is written atomically, and is readable only by the current user. It stores DID creation defaults and successful phone-slot assignments. It must contain user DIDs only; room numbers are managed separately.

Configure the defaults applied to newly imported DIDs with `vt module configure cucm`:

- `user-did-partition`
- `user-did-css`
- `user-did-voicemail-profile`
- `user-did-description-prefix`

Open `vt cucm dids` to browse or add inventory. The add prompt accepts comma-separated values or an equal-width numeric range:

```text
2313482000,2313482001
2313482000..2313482099
```

To assign a DID, open a phone and choose **Assign user DID to slot**, or select an existing line and choose **Assign available user DID**. Select an available DID, review whether the DN will be reused or created in CUCM, then press `Ctrl+Enter` or `Cmd+Enter` to save.

Availability is tracked by this local inventory. CUCM remains authoritative for the DN object and phone configuration. The module creates a missing DN using the stored defaults, updates the requested phone line index, and records the assignment locally only after the AXL update succeeds.

A user DID must have a route partition before it can be selected for assignment. Entries imported before their location-specific CUCM defaults are known remain visible as **Location defaults required** and cannot mutate CUCM. This prevents an ambiguous pattern-only assignment while the location mapping is unresolved.

The versioned JSON document is a temporary persistence boundary. A future DirSync or connector implementation can replace the local repository while retaining the same user-DID and assignment fields.