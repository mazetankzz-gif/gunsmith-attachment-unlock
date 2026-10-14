# The built tool

`GunsmithUnlockBuilder-1.1.exe` — the compiled builder, from the source in
[`../src/GunsmithUnlockBuilder`](../src/GunsmithUnlockBuilder).

**You do not need this to run Gunsmith.** It is optional, and the same file already ships inside
the [Gunsmith – Station Armory](https://www.lcpdfr.com/downloads/gta5mods/scripts/55489-gunsmith/)
download. It is kept here so the binary sits next to the source that built it, on the same tag,
and can be checked against a published hash.

## Verifying it

| | |
|---|---|
| Size | 45,568 bytes |
| Version | 1.1.0.0 |
| SHA-256 | `ecb27809c2c3c8235b4ff544dc1214893d8bf023c11e5d9006d284cbec7c8492` |
| MD5 | `388b8ac09a56a14805223e5afe9f0308` |

Byte-for-byte identical to the copy inside the Gunsmith 2.0 download, so either one checks against
the same hash.

```powershell
Get-FileHash .\GunsmithUnlockBuilder-1.1.exe -Algorithm SHA256
```

```bash
sha256sum GunsmithUnlockBuilder-1.1.exe
```

## Or build it yourself

.NET Framework 4.8, no dependencies:

```
dotnet build ../src/GunsmithUnlockBuilder/AttachmentUnlock.csproj -c Release
```

`../src/BuilderTests` runs the builder end to end against a game folder it generates itself and
checks the output — no game launch needed.

## Why a binary is in a source repository at all

Because the question this repository exists to answer is "what does that executable actually do",
and the most useful answer is the source *and* the exact binary, together, so the two can be
compared. `.gitignore` still keeps `bin/` and `obj/` out; this folder is a deliberate, versioned,
hash-documented artifact rather than whatever a dev build last produced.

New versions are added beside this one rather than replacing it, so a hash published anywhere
keeps pointing at the file it was published for.
