"""Offline-Build mit installiertem Rust, Windows SDK und .NET Framework."""
from pathlib import Path
import os
import subprocess
import sys

ROOT = Path(__file__).resolve().parent
BIN = ROOT / "bin"
BIN.mkdir(exist_ok=True)
SDK = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Windows Kits/10"
versions = sorted((p for p in (SDK / "Lib").iterdir() if p.name[0].isdigit()), key=lambda p: tuple(map(int, p.name.split("."))))
version = versions[-1].name
RUST = Path.home() / ".rustup/toolchains/stable-x86_64-pc-windows-msvc"
FW = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319"

def run(args, **kw):
    subprocess.run([str(arg) for arg in args], check=True, **kw)

run([SDK / "bin" / version / "x64/rc.exe", "/nologo", "/fo", BIN / "app.res", "resources.rc"], cwd=ROOT / "assets")
run([RUST / "bin/rustc.exe", ROOT / "src/main.rs", "--edition", "2024", "-C", "panic=abort", "-C", "opt-level=2",
     "-C", "linker=" + str(RUST / "lib/rustlib/x86_64-pc-windows-msvc/bin/rust-lld.exe"), "-C", "linker-flavor=lld-link",
     "-C", "link-arg=/entry:mainCRTStartup", "-C", "link-arg=/subsystem:windows", "-C", "link-arg=/nodefaultlib",
     "-C", "link-arg=" + str(BIN / "app.res"), "-L", "native=" + str(SDK / "Lib" / version / "um/x64"),
     "-o", BIN / "Mosaiks-Aufnahme.exe"])
winmd = SDK / "UnionMetadata" / version / "Windows.winmd"
refs = [FW / "WPF/PresentationFramework.dll", FW / "WPF/PresentationCore.dll", FW / "WPF/WindowsBase.dll",
        FW / "System.Xaml.dll", "System.Core.dll", "Microsoft.CSharp.dll", FW / "System.Runtime.dll",
        FW / "System.Runtime.WindowsRuntime.dll", FW / "System.Runtime.InteropServices.WindowsRuntime.dll", winmd]
run([FW / "csc.exe", "/nologo", "/target:winexe", "/platform:x64", "/optimize+", "/warnaserror+", "/codepage:65001",
     "/out:" + str(BIN / "Mosaiks-Capture-Engine.exe"), "/win32icon:" + str(ROOT / "assets/capture.ico"),
     "/win32manifest:" + str(ROOT / "assets/app.manifest"), *["/r:" + str(ref) for ref in refs],
     *sorted((ROOT / "engine/src").rglob("*.cs"))])
if "--test" in sys.argv:
    run([BIN / "Mosaiks-Aufnahme.exe", "--self-test"])
    report = ROOT / "engine-test.json"
    run([BIN / "Mosaiks-Capture-Engine.exe", "--self-test", report])
    print("Rust-Selbsttest: bestanden. Capture-Kern:", report.read_text(encoding="utf-8-sig"))
print("Gebaut:", BIN)
