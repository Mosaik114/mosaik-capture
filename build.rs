//! Build script: embeds the application icon, manifest and version information.
//!
//! Resource compilers are not available on every toolchain (`windres` is missing from a
//! plain rustup GNU install, `rc.exe` produces `.res` files that GNU ld cannot read). So
//! instead of shelling out, the resources are serialised into a COFF object with a
//! `.rsrc` section (see `build/rsrc.rs`). Both the MSVC linker and GNU ld accept that
//! object, so `*-windows-msvc` and `*-windows-gnu` builds share one code path and need
//! no external tools.

#[path = "build/rsrc.rs"]
mod rsrc;

use std::env;
use std::fs;
use std::path::PathBuf;

use rsrc::{Machine, RT_MANIFEST, RT_VERSION, Resources, VersionInfo};

const ICON_PATH: &str = "assets/capture.ico";
const MANIFEST_PATH: &str = "assets/app.manifest";
const RSRC_PATH: &str = "build/rsrc.rs";

/// Resource id of the icon group; the app loads it with `LoadImageW(instance, 1)`.
const APP_ICON_ID: u16 = 1;
/// `CREATEPROCESS_MANIFEST_RESOURCE_ID`: the manifest the loader applies to an exe.
const MANIFEST_ID: u16 = 1;
/// Version resources always use id 1.
const VERSION_ID: u16 = 1;

fn main() -> Result<(), String> {
    for path in ["build.rs", RSRC_PATH, ICON_PATH, MANIFEST_PATH] {
        println!("cargo:rerun-if-changed={path}");
    }

    if env_var("CARGO_CFG_TARGET_OS")? != "windows" {
        return Ok(());
    }
    let arch = env_var("CARGO_CFG_TARGET_ARCH")?;
    let machine = Machine::from_arch(&arch)
        .ok_or_else(|| format!("unsupported target architecture for resources: {arch}"))?;

    let mut resources = Resources::default();
    let icon = read(ICON_PATH)?;
    resources.add_icon(APP_ICON_ID, &icon).map_err(|error| format!("{ICON_PATH}: {error}"))?;
    resources.add(RT_MANIFEST, MANIFEST_ID, read(MANIFEST_PATH)?)?;
    let version = VersionInfo {
        version: [
            version_part("CARGO_PKG_VERSION_MAJOR")?,
            version_part("CARGO_PKG_VERSION_MINOR")?,
            version_part("CARGO_PKG_VERSION_PATCH")?,
        ],
        file_description: "Mosaik Capture",
        product_name: "Mosaik Capture",
        original_filename: "MosaikCapture.exe",
    };
    resources.add(RT_VERSION, VERSION_ID, version.to_resource())?;

    let object = rsrc::coff_object(machine, &resources);
    // Same COFF content either way; just the conventional extension per toolchain.
    let file_name =
        if env_var("CARGO_CFG_TARGET_ENV")? == "msvc" { "resources.obj" } else { "resources.o" };
    let out = PathBuf::from(env_var("OUT_DIR")?).join(file_name);
    fs::write(&out, object).map_err(|error| format!("{}: {error}", out.display()))?;
    // All linked targets, not just the binary: the unit tests then run with the same
    // manifest (Common Controls v6, PerMonitorV2) and icon as the shipped exe.
    println!("cargo:rustc-link-arg={}", out.display());
    Ok(())
}

fn env_var(name: &str) -> Result<String, String> {
    env::var(name).map_err(|error| format!("{name}: {error}"))
}

fn read(path: &str) -> Result<Vec<u8>, String> {
    fs::read(path).map_err(|error| format!("{path}: {error}"))
}

/// A version component, which the version resource stores as 16 bits.
fn version_part(name: &str) -> Result<u16, String> {
    let value = env_var(name)?;
    value
        .parse()
        .map_err(|_| format!("{name}={value} does not fit the 16-bit version resource field"))
}
