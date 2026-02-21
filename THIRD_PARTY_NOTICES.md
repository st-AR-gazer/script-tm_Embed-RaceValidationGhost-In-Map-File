# Third-Party Notices

This project uses third-party components. The notices below summarize the license obligations relevant to this repository and its runtime workflow.

## GBX.NET

- Package: `GBX.NET`
- Version used here: `2.3.0-nightly.20260128.cca24500`
- Project URL: https://github.com/BigBang1112/gbx-net
- License: MIT

This project links against `GBX.NET` directly. `GBX.NET` is MIT-licensed.

## gbxlzo

- Tool: `gbxlzo.exe`
- Use in this project: external runtime executable invoked via process call (`--gbxlzo` / auto-discovery)
- Common distribution license: GNU GPL v3

This project does not statically or dynamically link `gbxlzo`; it executes it as a separate program for compression/decompression. If you redistribute `gbxlzo.exe` with this project (or in your release bundle), GPLv3 obligations apply to that redistributed `gbxlzo` binary.

The GPLv3 license text is included at:

- `LICENSES/GPL-3.0.txt`

## Practical Compliance For This Repo

- Distributing only this project's code/binary (without bundling `gbxlzo.exe`): follow this project's own licensing and MIT third-party notice for `GBX.NET`.
- Distributing this project together with `gbxlzo.exe`: include GPLv3 notice/text for `gbxlzo` and satisfy GPLv3 redistribution requirements for that tool.
