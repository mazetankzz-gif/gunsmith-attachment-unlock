# Gunsmith Attachment Unlock — builder

The source for `GunsmithUnlockBuilder.exe`, the optional tool that ships alongside
[Gunsmith – Station Armory](https://www.lcpdfr.com/downloads/gta5mods/scripts/55489-gunsmith/)
for GTA V / LSPDFR.

Current release: **builder 1.1**, shipped in Gunsmith 2.0. The builder itself is unchanged since
Gunsmith 1.1 — 2.0 is a plugin update and does not alter how attachments are unlocked.

**The built tool:** [`release/GunsmithUnlockBuilder-1.1.exe`](release/GunsmithUnlockBuilder-1.1.exe)
— the same file that ships inside the Gunsmith download, with its SHA-256 published in
[release/README.md](release/README.md) so either copy can be checked. You do not need it to run
Gunsmith; it is optional.

It is published here so anyone can read exactly what it does before running it — including
LCPDFR staff, who ask for the source of any executable hosted on the site.

## What it does

Vanilla GTA V only lets each weapon take a handful of attachments. A carbine has one optic.
This tool rewrites the attachment lists so every gun can take every attachment that physically
fits it — that same carbine ends up with twenty.

It works in three steps:

1. Reads the player's **own** `weapons.meta` files out of their `mods` folder (and the game's
   archives), including whatever other weapon mods they already have installed.
2. Works out which attachments each weapon can actually mount, and widens the lists.
3. Writes an OpenIV `.oiv` package from the result, which the player installs themselves.

## Why it's a builder and not a ready-made download

This is the part worth understanding, because it is the whole reason the tool exists.

A pre-built `.oiv` would have to contain **someone else's** `weapons.meta` files. On a modded
install those files belong to other mods — graphics overhauls, weapon packs — and some of them
are explicitly marked *do not distribute without permission of the creator*. Shipping them would
be redistributing other people's work, and it would also **overwrite whatever the player had
already changed**.

So the tool builds the package on the player's machine, from the player's own files. Nothing
belonging to anyone else is ever redistributed, and nobody's existing setup gets flattened.

It also writes a **revert** package next to it, so the change can be undone in one click.

## What it does not do

- No network access. It never phones home, downloads anything, or sends any data anywhere.
- It does not modify the game directly. It writes an `.oiv` and stops; installing it is the
  player's decision, through OpenIV.
- It does not touch anything outside the GTA V folder it is pointed at, and the folder it
  writes the finished package to.

## Building it

.NET Framework 4.8. No dependencies.

```
dotnet build src/GunsmithUnlockBuilder/AttachmentUnlock.csproj -c Release
```

`src/BuilderTests` is a self-contained check suite that runs the builder against a real game
folder and verifies the output — no game launch required.

## Writing a plugin that talks to Gunsmith

Unrelated to this tool, but this is where people look. Gunsmith 2.0 exposes an **Open Armory**
API so another LSPDFR plugin can file its own shootout and have ballistics close it months
later, or ask whether the officer is qualified on what they are carrying, how many rounds they
have fired this shift, or whether they are on restricted duty.

It is reflection-only: one drop-in file, no reference to `Gunsmith.dll`, and your plugin keeps
working when Gunsmith is not installed. The kit — `GunsmithBridge.cs` and `MODDER_API.md` — is
folder 4 of the Gunsmith download.

## Licence

MIT. See [LICENSE](LICENSE).

The **Warzone Attachments Pack** by Evandrotorquato is a separate mod with its own terms and is
**not** included here or in the Gunsmith download; Gunsmith only helps set it up if the player
downloads it themselves.
