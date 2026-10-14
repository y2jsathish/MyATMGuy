# XFS probe (atmxfsprobe)

A tiny 32-bit, read-only tool that talks to the terminal's own XFS manager (`msxfs.dll`). It opens one logical service, prints the raw status and capabilities, and closes it. It never sends a command that moves a card, cash or a PIN.

## Why 32-bit

On the DN 200V the XFS manager, the service providers and their registry keys are all 32-bit (`HKLM\SOFTWARE\WOW6432Node\XFS`). A 64-bit process cannot load them, so the production device adapter will be a 32-bit helper process; this probe is its first, smallest version.

## Before you run it

1. Stop Kalignite so it does not own the devices. The Diebold provider (`dbdspi.dll`) may need the Diebold device service (`DbdDevService`) to be running; if the open call fails with -15 while it is stopped, start it and try again.
2. Pick a logical service name that already exists. The KAL XFS manager reads logical services from `HKEY_USERS\.DEFAULT\XFS\LOGICAL_SERVICES` (the machine's `HKLM\SOFTWARE\WOW6432Node\XFS` holds only the providers), so nothing needs to be registered. List them with `reg query "HKEY_USERS\.DEFAULT\XFS\LOGICAL_SERVICES" /s`. On the DN 200V the DIP card reader is `DN_SecDipCardRdr` (class `IDC`, provider `DN_SecDipCardRdr`, backed by `dbdspi.dll`).

If you earlier created `HKLM\SOFTWARE\XFS\LOGICAL_SERVICES\ATM_IDC` (or the doubled `WOW6432Node\WOW6432Node` path), it is unused and can be removed:

```
reg delete "HKLM\SOFTWARE\XFS\LOGICAL_SERVICES\ATM_IDC" /f /reg:32
reg delete "HKLM\SOFTWARE\WOW6432Node\XFS\LOGICAL_SERVICES\ATM_IDC" /f /reg:32
```

## Run

```
atmxfsprobe.exe DN_SecDipCardRdr
```

The manager on this terminal starts with XFS 2.00, and the probe tries every version range in both word orders until one is accepted. Send back the console output. The structure layouts follow the XFS headers but have not yet been checked against a real service provider, so the output prints raw numbers next to the interpretation; a mismatch will show up there and is useful information.
