"""Pack the Windows release sources from a Git ref and two public upstream commits."""

import argparse
import hashlib
from pathlib import Path
import subprocess
import tarfile
from tempfile import TemporaryDirectory
from zipfile import ZIP_DEFLATED, ZipFile


APP = "source/final-three-in-one/"
PROJECT = [
    "README.md", "LICENSE", "NOTICE.md", "ACKNOWLEDGEMENTS.md", "global.json",
    "docs/RELEASE-v4.0.md", "docs/physical-inputs.md", APP + "README.md",
    APP + "LICENSE", APP + "windows/v60_viiper_app",
    APP + "experiments/windows-wireless-pro1", APP + "tools/tests/pure_software_core_test",
    APP + "tools/tests/Test-TrayBehavior.ps1",
    APP + "tools/viiper/haptic-src", APP + "tools/usbip-win2/v0.9.7.7",
    APP + "tools/viiper/haptic-v0.8.0/LICENSE.txt",
    APP + "tools/viiper/haptic-v0.8.0/viiper-haptic-v2.8.exe",
]
STACK_COMMIT = "e38553977a25fb0b55b383c72c289be0975f422c"
BTKEY_COMMIT = "4b49cb955d08dc80a82673083fc0463cbc0c2540"
STACK_INPUTS = [
    "LICENSE", "src", "platform/embedded", "platform/posix", "platform/windows",
    "chipset/realtek", "3rd-party/bluedroid", "3rd-party/rijndael", "3rd-party/yxml",
]
BTKEY_INPUTS = STACK_INPUTS + [
    "example/Makefile.inc", "example/btkeyLib.c", "example/btkeyLib.h",
    "3rd-party/hxcmod-player/hxcmod.h", "3rd-party/hxcmod-player/mods/mod.h",
    "3rd-party/hxcmod-player/readme.txt",
    "3rd-party/micro-ecc", "3rd-party/tinydir", "port/windows-winusb/Makefile",
    "port/windows-winusb/btstack_config.h", "port/windows-winusb/main.c",
]
FIRMWARE = {
    "rtl8852cu_fw": "9b8b27741a914c1cecc1ab9837194236890522101a57ba1898eb55f8d538dae0",
    "LICENCE.rtlwifi_firmware.txt": "a61351665b4f264f6c631364f85b907d8f8f41f8b369533ef4021765f9f3b62e",
}


def add_git_files(out, checkout, revision, paths, prefix, scratch):
    """git archive only reads committed files and preserves their upstream bytes."""
    archive = scratch / "selected.tar"
    subprocess.run([
        "git", "-c", "core.autocrlf=false", "-C", str(checkout), "archive",
        "--format=tar", "-o", str(archive), revision, "--", *paths,
    ], check=True)
    hashes = {}
    with tarfile.open(archive, "r:") as source:
        for entry in source:
            if entry.isdir():
                continue
            if not entry.isfile():
                raise ValueError(f"Unexpected source entry: {entry.name}")
            data = source.extractfile(entry).read()
            out.writestr(prefix + entry.name, data)
            if prefix:
                hashes[entry.name] = hashlib.sha256(data).hexdigest()
    if prefix:
        contents = "".join(f"{hashes[path]}  {path}\n" for path in sorted(hashes))
        manifest = contents.encode("utf-8")
        out.writestr(prefix + "BUILD-INPUTS.sha256", manifest)
        out.writestr(prefix + "BTSTACK_COMMIT.txt", revision + "\n")
        print(f"{prefix}BUILD-INPUTS.sha256: {hashlib.sha256(manifest).hexdigest()}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("repo", "btstack", "btkey", "firmware", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--ref", required=True)
    args = parser.parse_args()
    completed = subprocess.check_output(
        ["git", "-C", str(args.repo), "rev-parse", "--verify", args.ref + "^{commit}"],
        text=True,
    ).strip()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(dir=args.output.parent) as temporary:
        scratch = Path(temporary)
        package = scratch / "source.zip"
        with ZipFile(package, "w", ZIP_DEFLATED, compresslevel=6) as out:
            add_git_files(out, args.repo, completed, PROJECT, "", scratch)
            out.writestr("SOURCE_COMMIT.txt", completed + "\n")
            add_git_files(out, args.btstack, STACK_COMMIT, STACK_INPUTS, "third_party/btstack/", scratch)
            add_git_files(out, args.btkey, BTKEY_COMMIT, BTKEY_INPUTS, "third_party/btkey-btstack/", scratch)
            for name, expected in FIRMWARE.items():
                data = (args.firmware / name).read_bytes()
                if hashlib.sha256(data).hexdigest() != expected:
                    raise ValueError(f"Unexpected firmware distribution file: {name}")
                out.writestr("third_party/realtek-firmware/" + name, data)
        package.replace(args.output)
    print(f"{completed} -> {args.output}")


if __name__ == "__main__":
    main()
