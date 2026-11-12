//! Build script: embeds the application icon, manifest and version information.
//!
//! Resource compilers are not available on every toolchain (`windres` is missing from a
//! plain rustup GNU install, `rc.exe` produces `.res` files that GNU ld cannot read). So
//! instead of shelling out, this script serializes the resources itself into a COFF
//! object with a single `.rsrc` section — the same thing `cvtres`/`windres` emit. Both
//! the MSVC linker and GNU ld accept that object, so `*-windows-msvc` and
//! `*-windows-gnu` builds share one code path and need no external tools.

use std::collections::BTreeMap;
use std::env;
use std::fs;
use std::path::PathBuf;

const ICON_PATH: &str = "assets/capture.ico";
const MANIFEST_PATH: &str = "assets/app.manifest";

const RT_ICON: u16 = 3;
const RT_GROUP_ICON: u16 = 14;
const RT_VERSION: u16 = 16;
const RT_MANIFEST: u16 = 24;

/// Language of all resources (en-US, what `rc.exe` assigns by default).
const LANGUAGE: u16 = 0x0409;
/// Resource id of the icon group; the app loads it with `LoadIconW(instance, 1)`.
const APP_ICON_ID: u16 = 1;
/// `CREATEPROCESS_MANIFEST_RESOURCE_ID`: the manifest the loader applies to an exe.
const MANIFEST_ID: u16 = 1;

fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-changed={ICON_PATH}");
    println!("cargo:rerun-if-changed={MANIFEST_PATH}");

    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    let arch = env::var("CARGO_CFG_TARGET_ARCH").expect("cargo sets CARGO_CFG_TARGET_ARCH");
    let machine = Machine::from_arch(&arch)
        .unwrap_or_else(|| panic!("unsupported target architecture for resources: {arch}"));

    let mut resources = Resources::default();
    let icon = fs::read(ICON_PATH).expect("read application icon");
    add_icon(&mut resources, APP_ICON_ID, &icon);
    resources.add(
        RT_MANIFEST,
        MANIFEST_ID,
        fs::read(MANIFEST_PATH).expect("read manifest"),
    );
    resources.add(RT_VERSION, 1, version_info());

    let object = coff_object(machine, &resources);
    // Same COFF content either way; just the conventional extension per toolchain.
    let file_name = if env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc") {
        "resources.obj"
    } else {
        "resources.o"
    };
    let out = PathBuf::from(env::var_os("OUT_DIR").expect("cargo sets OUT_DIR")).join(file_name);
    fs::write(&out, object).expect("write resource object");
    println!("cargo:rustc-link-arg-bins={}", out.display());
}

/// Resources keyed by type id, then by name id. Ordered maps give the sorted entry
/// order the PE resource directory requires.
#[derive(Default)]
struct Resources(BTreeMap<u16, BTreeMap<u16, Vec<u8>>>);

impl Resources {
    fn add(&mut self, kind: u16, id: u16, data: Vec<u8>) {
        self.0.entry(kind).or_default().insert(id, data);
    }
}

/// Splits an `.ico` file into one `RT_ICON` per image plus the `RT_GROUP_ICON`
/// directory that references them.
fn add_icon(resources: &mut Resources, group_id: u16, ico: &[u8]) {
    let count = read_u16(ico, 4);
    let mut group = Vec::new();
    push_u16(&mut group, 0); // reserved
    push_u16(&mut group, 1); // type: icon
    push_u16(&mut group, count);
    for index in 0..count {
        let entry = &ico[6 + 16 * usize::from(index)..][..16];
        let size = read_u32(entry, 8);
        let offset = read_u32(entry, 12);
        let image_id = index + 1;
        let start = usize::try_from(offset).expect("icon offset fits usize");
        let end = start + usize::try_from(size).expect("icon size fits usize");
        resources.add(RT_ICON, image_id, ico[start..end].to_vec());

        // GRPICONDIRENTRY: the ICONDIRENTRY fields with the file offset replaced by the id.
        group.extend_from_slice(&entry[..4]); // width, height, colours, reserved
        let planes = read_u16(entry, 4);
        push_u16(&mut group, planes.max(1));
        group.extend_from_slice(&entry[6..12]); // bit count, image size
        push_u16(&mut group, image_id);
    }
    resources.add(RT_GROUP_ICON, group_id, group);
}

/// Builds a `VS_VERSIONINFO` block from the package metadata.
fn version_info() -> Vec<u8> {
    let part = |name: &str| -> u32 {
        env::var(name)
            .ok()
            .and_then(|value| value.parse().ok())
            .unwrap_or(0)
    };
    let (major, minor, patch) = (
        part("CARGO_PKG_VERSION_MAJOR"),
        part("CARGO_PKG_VERSION_MINOR"),
        part("CARGO_PKG_VERSION_PATCH"),
    );
    let version = env::var("CARGO_PKG_VERSION").unwrap_or_default();

    let mut fixed = Vec::new();
    for value in [
        0xFEEF_04BD,           // signature
        0x0001_0000,           // structure version
        (major << 16) | minor, // file version (high)
        patch << 16,           // file version (low)
        (major << 16) | minor, // product version (high)
        patch << 16,           // product version (low)
        0x3F,                  // file flags mask
        0,                     // file flags
        0x0004_0004,           // VOS_NT_WINDOWS32
        1,                     // VFT_APP
        0,                     // subtype
        0,                     // date (high)
        0,                     // date (low)
    ] {
        push_u32(&mut fixed, value);
    }

    let strings = [
        ("FileDescription", "Mosaiks Aufnahme"),
        ("ProductName", "Mosaiks Aufnahme"),
        ("FileVersion", version.as_str()),
        ("ProductVersion", version.as_str()),
        ("OriginalFilename", "Mosaiks-Aufnahme.exe"),
    ]
    .map(|(key, value)| version_node(key, NodeValue::Text(value), &[]));
    // German (0x0407), Unicode code page 1200 (0x04B0).
    let table = version_node("040704B0", NodeValue::None, &strings);
    let string_info = version_node("StringFileInfo", NodeValue::None, &[table]);
    let mut translation = Vec::new();
    push_u16(&mut translation, 0x0407);
    push_u16(&mut translation, 1200);
    let var = version_node("Translation", NodeValue::Binary(&translation), &[]);
    let var_info = version_node("VarFileInfo", NodeValue::None, &[var]);

    version_node(
        "VS_VERSION_INFO",
        NodeValue::Binary(&fixed),
        &[string_info, var_info],
    )
}

#[derive(Clone, Copy)]
enum NodeValue<'a> {
    None,
    Text(&'a str),
    Binary(&'a [u8]),
}

/// Serializes one node of the version resource tree (`wLength`, `wValueLength`,
/// `wType`, key, value, children), each part aligned to 4 bytes.
fn version_node(key: &str, value: NodeValue<'_>, children: &[Vec<u8>]) -> Vec<u8> {
    let (value_bytes, value_length, kind) = match value {
        // rc.exe marks value-less container nodes as text, too.
        NodeValue::None => (Vec::new(), 0, 1),
        NodeValue::Binary(bytes) => (bytes.to_vec(), bytes.len(), 0),
        NodeValue::Text(text) => {
            let units: Vec<u16> = text.encode_utf16().chain([0]).collect();
            let bytes = units.iter().flat_map(|unit| unit.to_le_bytes()).collect();
            (bytes, units.len(), 1)
        }
    };
    let mut node = vec![0; 6];
    for unit in key.encode_utf16().chain([0]) {
        push_u16(&mut node, unit);
    }
    align(&mut node, 4);
    node.extend_from_slice(&value_bytes);
    for child in children {
        align(&mut node, 4);
        node.extend_from_slice(child);
    }
    let length = u16::try_from(node.len()).expect("version node fits u16");
    node[0..2].copy_from_slice(&length.to_le_bytes());
    let value_length = u16::try_from(value_length).expect("version value fits u16");
    node[2..4].copy_from_slice(&value_length.to_le_bytes());
    node[4..6].copy_from_slice(&u16::to_le_bytes(kind));
    node
}

#[derive(Clone, Copy)]
enum Machine {
    X86,
    X64,
    Arm64,
}

impl Machine {
    fn from_arch(arch: &str) -> Option<Self> {
        match arch {
            "x86" => Some(Self::X86),
            "x86_64" => Some(Self::X64),
            "aarch64" => Some(Self::Arm64),
            _ => None,
        }
    }

    fn id(self) -> u16 {
        match self {
            Self::X86 => 0x014C,
            Self::X64 => 0x8664,
            Self::Arm64 => 0xAA64,
        }
    }

    /// Relocation type for a 32-bit image-relative address (`ADDR32NB`/`DIR32NB`).
    fn rva_relocation(self) -> u16 {
        match self {
            Self::X86 => 0x0007,
            Self::X64 => 0x0003,
            Self::Arm64 => 0x0002,
        }
    }
}

/// Lays out the three-level resource directory (type → name → language), the data
/// entries and the raw data. Returns the section bytes and the offsets of the fields
/// that hold RVAs and therefore need relocations.
fn rsrc_section(resources: &Resources) -> (Vec<u8>, Vec<u32>) {
    const DIRECTORY: usize = 16;
    const ENTRY: usize = 8;
    const DATA_ENTRY: usize = 16;
    const SUBDIRECTORY: u32 = 0x8000_0000;
    let types = &resources.0;
    let name_count: usize = types.values().map(BTreeMap::len).sum();

    let names_start = DIRECTORY + ENTRY * types.len();
    let languages_start = names_start
        + types
            .values()
            .map(|names| DIRECTORY + ENTRY * names.len())
            .sum::<usize>();
    let data_entries_start = languages_start + name_count * (DIRECTORY + ENTRY);
    let data_start = data_entries_start + name_count * DATA_ENTRY;
    let offset = |value: usize| u32::try_from(value).expect("resource section fits u32");

    let mut out = Vec::new();
    let mut relocations = Vec::new();

    push_directory_header(&mut out, types.len());
    let mut names_offset = names_start;
    for (&kind, names) in types {
        push_u32(&mut out, u32::from(kind));
        push_u32(&mut out, SUBDIRECTORY | offset(names_offset));
        names_offset += DIRECTORY + ENTRY * names.len();
    }

    let mut index = 0;
    for names in types.values() {
        push_directory_header(&mut out, names.len());
        for &id in names.keys() {
            push_u32(&mut out, u32::from(id));
            let language_dir = languages_start + index * (DIRECTORY + ENTRY);
            push_u32(&mut out, SUBDIRECTORY | offset(language_dir));
            index += 1;
        }
    }

    for index in 0..name_count {
        push_directory_header(&mut out, 1);
        push_u32(&mut out, u32::from(LANGUAGE));
        push_u32(&mut out, offset(data_entries_start + index * DATA_ENTRY));
    }

    let mut data_offset = data_start;
    for data in types.values().flat_map(BTreeMap::values) {
        relocations.push(offset(out.len()));
        push_u32(&mut out, offset(data_offset)); // RVA, completed by the linker
        push_u32(&mut out, offset(data.len()));
        push_u32(&mut out, 0); // code page
        push_u32(&mut out, 0); // reserved
        data_offset += data.len().next_multiple_of(8);
    }

    for data in types.values().flat_map(BTreeMap::values) {
        out.extend_from_slice(data);
        align(&mut out, 8);
    }
    (out, relocations)
}

fn push_directory_header(out: &mut Vec<u8>, id_entries: usize) {
    push_u32(out, 0); // characteristics
    push_u32(out, 0); // time stamp
    push_u16(out, 0); // major version
    push_u16(out, 0); // minor version
    push_u16(out, 0); // named entries
    push_u16(
        out,
        u16::try_from(id_entries).expect("entry count fits u16"),
    );
}

/// Wraps the `.rsrc` section into a relocatable COFF object with one section symbol.
fn coff_object(machine: Machine, resources: &Resources) -> Vec<u8> {
    const FILE_HEADER: usize = 20;
    const SECTION_HEADER: usize = 40;
    const RELOCATION: usize = 10;
    const SECTION_NAME: [u8; 8] = *b".rsrc\0\0\0";
    // IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_MEM_READ
    const SECTION_FLAGS: u32 = 0x4000_0040;

    let (section, relocations) = rsrc_section(resources);
    let to_u32 = |value: usize| u32::try_from(value).expect("object fits u32");
    let raw_data = FILE_HEADER + SECTION_HEADER;
    let relocations_start = raw_data + section.len();
    let symbols_start = relocations_start + RELOCATION * relocations.len();
    let relocation_count = u16::try_from(relocations.len()).expect("relocations fit u16");

    let mut out = Vec::new();
    push_u16(&mut out, machine.id());
    push_u16(&mut out, 1); // sections
    push_u32(&mut out, 0); // time stamp (reproducible builds)
    push_u32(&mut out, to_u32(symbols_start));
    push_u32(&mut out, 2); // section symbol + its auxiliary record
    push_u16(&mut out, 0); // optional header size
    push_u16(&mut out, 0); // characteristics

    out.extend_from_slice(&SECTION_NAME);
    push_u32(&mut out, 0); // virtual size
    push_u32(&mut out, 0); // virtual address
    push_u32(&mut out, to_u32(section.len()));
    push_u32(&mut out, to_u32(raw_data));
    push_u32(&mut out, to_u32(relocations_start));
    push_u32(&mut out, 0); // line numbers
    push_u16(&mut out, relocation_count);
    push_u16(&mut out, 0); // line number count
    push_u32(&mut out, SECTION_FLAGS);

    out.extend_from_slice(&section);
    for field in relocations {
        push_u32(&mut out, field);
        push_u32(&mut out, 0); // symbol index: the section symbol
        push_u16(&mut out, machine.rva_relocation());
    }

    // Section symbol (IMAGE_SYM_CLASS_STATIC) followed by its section-definition record.
    out.extend_from_slice(&SECTION_NAME);
    push_u32(&mut out, 0); // value
    push_u16(&mut out, 1); // section number
    push_u16(&mut out, 0); // type
    out.push(3); // storage class: static
    out.push(1); // auxiliary records
    push_u32(&mut out, to_u32(section.len()));
    push_u16(&mut out, relocation_count);
    push_u16(&mut out, 0); // line numbers
    push_u32(&mut out, 0); // checksum
    push_u16(&mut out, 0); // associated section
    out.push(0); // COMDAT selection
    out.extend_from_slice(&[0; 3]);

    push_u32(&mut out, 4); // empty string table
    out
}

fn read_u16(bytes: &[u8], at: usize) -> u16 {
    u16::from_le_bytes([bytes[at], bytes[at + 1]])
}

fn read_u32(bytes: &[u8], at: usize) -> u32 {
    u32::from_le_bytes(bytes[at..at + 4].try_into().expect("four bytes"))
}

fn push_u16(out: &mut Vec<u8>, value: u16) {
    out.extend_from_slice(&value.to_le_bytes());
}

fn push_u32(out: &mut Vec<u8>, value: u32) {
    out.extend_from_slice(&value.to_le_bytes());
}

fn align(out: &mut Vec<u8>, alignment: usize) {
    out.resize(out.len().next_multiple_of(alignment), 0);
}
