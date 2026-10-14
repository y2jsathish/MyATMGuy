# XFS probe (atmxfsprobe)

A tiny 32-bit, read-only tool that talks to the terminal's own XFS manager (`msxfs.dll`). It opens one logical service, prints the raw status and capabilities, and closes it. It never sends a command that moves a card, cash or a PIN.

## Why 32-bit

On the DN 200V the XFS manager, the service providers and their registry keys are all 32-bit (`HKLM\SOFTWARE\WOW6432Node\XFS`). A 64-bit process cannot load them, so the production device adapter will be a 32-bit helper process; this probe is its first, smallest version.

## Before you run it

1. Stop Kalignite and any Diebold device services (`DbdDevService`, `Diebold.Harvester.Service`). Only one program can own a device at a time.
2. A logical service must be registered. On the DN 200V none are today (the KAL XFS trace shows `LOGICAL_SERVICES\<name>` not found). To register the DIP card reader, an administrator runs the two commands below. **This writes to the registry; review before running.** Undo with the delete command.

```
reg add "HKLM\SOFTWARE\WOW6432Node\XFS\LOGICAL_SERVICES\ATM_IDC" /v class /t REG_SZ /d IDC /reg:32
reg add "HKLM\SOFTWARE\WOW6432Node\XFS\LOGICAL_SERVICES\ATM_IDC" /v provider /t REG_SZ /d DN_DIPCardRdr /reg:32
```

Undo:

```
reg delete "HKLM\SOFTWARE\WOW6432Node\XFS\LOGICAL_SERVICES\ATM_IDC" /f /reg:32
```

## Run

```
atmxfsprobe.exe ATM_IDC
```

Send back the console output. The structure layouts follow the XFS 3.x headers but have not yet been checked against a real service provider, so the output prints raw numbers next to the interpretation; a mismatch will show up there and is useful information.
