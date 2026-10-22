"""Zips one runtime's publish of pilot v2 as a release asset, with the VERSION marker that makes it an installed release.

    python .github/release-zip.py <publish folder> <version> <rid> <zip>

The publish is either native AOT (a tagged release: four executables and a few native libraries) or framework-dependent
(the beta channel: the same four executables as app hosts, beside their .dll and the libraries they reference).

The marker (src/Pilot.Node/Updates/ReleaseInstall.cs reads it) is "<version> <rid>", then every file the zip holds, a
line each, relative and written with /: a node updates only an install whose marker names its own version, and an
update deletes only the files the old marker listed. Symbols and XML docs are left out. Entries are written with / and,
on Linux, with their modes, so an executable stays one; Windows' Compress-Archive does neither reliably.
"""
import os
import stat
import sys
import zipfile

LEFT_OUT = (".pdb", ".xml", ".dbg")


def main(publish, version, rid, out):
    files = []
    for folder, _, names in os.walk(publish):
        for name in names:
            path = os.path.join(folder, name)
            rel = os.path.relpath(path, publish).replace(os.sep, "/")
            if rel.endswith(LEFT_OUT) or ".staticwebassets." in rel or rel == "VERSION":
                continue
            files.append(rel)
    files.sort()
    # The four executables, as their name alone (Linux) or with .exe (Windows). A framework-dependent publish (the beta
    # channel's) also holds pilot.dll and pilot.runtimeconfig.json beside each, which are not executables.
    names = ("pilot", "pilot-node", "pilot-gui", "pilot-tui")
    exe = ".exe" if rid.startswith("win-") else ""
    executables = [f for f in files if f in tuple(n + exe for n in names)]
    if len(executables) != 4:
        sys.exit(f"the publish has {executables}, not pilot, pilot-node, pilot-gui and pilot-tui")
    marker = "\n".join([f"{version} {rid}"] + sorted(files + ["VERSION"])) + "\n"
    with open(os.path.join(publish, "VERSION"), "w", newline="\n") as f:
        f.write(marker)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for rel in files + ["VERSION"]:
            path = os.path.join(publish, rel)
            info = zipfile.ZipInfo.from_file(path, rel)
            if not rid.startswith("win-") and rel in executables:
                info.external_attr = (stat.S_IFREG | 0o755) << 16
            with open(path, "rb") as source:
                z.writestr(info, source.read(), zipfile.ZIP_DEFLATED)
    print(marker)


if __name__ == "__main__":
    if len(sys.argv) != 5:
        sys.exit(__doc__)
    main(*sys.argv[1:])
