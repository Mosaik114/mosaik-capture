//! Serialises Win32 resources (icons, manifest, version information) into a COFF
//! object with a single `.rsrc` section — the same thing `cvtres`/`windres` emit.
//!
//! The module is pure: `build.rs` feeds it file contents and writes the result. It is
//! also compiled into the crate's unit tests (see `src/main.rs`) so the binary layout
//! is covered by `cargo test`.

use std::collections::BTreeMap;
use std::collections::btree_map::Entry;

/// Resource type of a single icon image.
pub const RT_ICON: u16 = 3;
/// Resource type of an icon directory referencing [`RT_ICON`] images.
pub const RT_GROUP_ICON: u16 = 14;
/// Resource type of version information.
pub const RT_VERSION: u16 = 16;
/// Resource type of an application manifest.
pub const RT_MANIFEST: u16 = 24;

/// Language of all resources: German (Germany), matching the version translation.
pub const LANGUAGE: u16 = 0x0407;
/// Unicode code page, used in the version translation.
const CODE_PAGE_UNICODE: u16 = 1200;

const ICONDIR_SIZE: usize = 6;
const ICONDIRENTRY_SIZE: usize = 16;

/// Resources keyed by type id, then by name id. Ordered maps give the sorted entry
/// order the PE resource directory requires.
#[derive(Debug, Default)]
pub struct Resources(BTreeMap<u16, BTreeMap<u16, Vec<u8>>>);

impl Resources {
    /// Adds one resource; a second resource with the same type and id is an error.
    pub fn add(&mut self, kind: u16, id: u16, data: Vec<u8>) -> Result<(), String> {
        match self.0.entry(kind).or_default().entry(id) {
            Entry::Occupied(_) => Err(format!("duplicate resource: type {kind}, id {id}")),
            Entry::Vacant(slot) => {
                slot.insert(data);
                Ok(())
            }
        }
    }

    /// The first free [`RT_ICON`] id after all existing ones, so several icon groups
    /// never share image ids.
    fn next_icon_id(&self) -> u16 {
        self.0
            .get(&RT_ICON)
            .and_then(|images| images.last_key_value())
            .map_or(1, |(&last, _)| last.saturating_add(1))
    }

    /// Splits an `.ico` file into one [`RT_ICON`] per image plus the
    /// [`RT_GROUP_ICON`] directory `group_id` that references them. Image ids continue
    /// after those of previously added icons. Nothing is added on error.
    pub fn add_icon(&mut self, group_id: u16, ico: &[u8]) -> Result<(), String> {
        let header = ico.get(..ICONDIR_SIZE).ok_or("icon file is shorter than its header")?;
        if read_u16(header, 0) != 0 || read_u16(header, 2) != 1 {
            return Err("not an icon file (ICONDIR reserved/type must be 0/1)".into());
        }
        let count = read_u16(header, 4);
        if count == 0 {
            return Err("icon file contains no images".into());
        }
        if self.0.get(&RT_GROUP_ICON).is_some_and(|groups| groups.contains_key(&group_id)) {
            return Err(format!("duplicate icon group {group_id}"));
        }
        let first_id = self.next_icon_id();
        if u32::from(first_id) + u32::from(count) > u32::from(u16::MAX) {
            return Err("too many icon images".into());
        }

        let mut group = Vec::new();
        push_u16(&mut group, 0); // reserved
        push_u16(&mut group, 1); // type: icon
        push_u16(&mut group, count);
        let mut images = Vec::new();
        for index in 0..count {
            let start = ICONDIR_SIZE + ICONDIRENTRY_SIZE * usize::from(index);
            let entry = ico
                .get(start..start + ICONDIRENTRY_SIZE)
                .ok_or_else(|| format!("icon directory entry {index} is truncated"))?;
            let size = to_usize(read_u32(entry, 8));
            let offset = to_usize(read_u32(entry, 12));
            let image = offset
                .checked_add(size)
                .and_then(|end| ico.get(offset..end))
                .ok_or_else(|| format!("icon image {index} lies outside the file"))?;
            let image_id = first_id + index;
            images.push((image_id, image.to_vec()));

            // GRPICONDIRENTRY: the ICONDIRENTRY with the file offset replaced by the id.
            group.extend_from_slice(&entry[..4]); // width, height, colours, reserved
            push_u16(&mut group, read_u16(entry, 4).max(1)); // planes
            group.extend_from_slice(&entry[6..12]); // bit count, image size
            push_u16(&mut group, image_id);
        }
        for (id, image) in images {
            self.add(RT_ICON, id, image)?;
        }
        self.add(RT_GROUP_ICON, group_id, group)
    }
}

/// Inputs of the version resource.
#[derive(Debug)]
pub struct VersionInfo<'a> {
    /// Major, minor and patch version.
    pub version: [u16; 3],
    /// Shown as "File description" in Explorer.
    pub file_description: &'a str,
    /// Shown as "Product name" in Explorer.
    pub product_name: &'a str,
    /// File name the executable was built as.
    pub original_filename: &'a str,
}

impl VersionInfo<'_> {
    /// Builds the `VS_VERSIONINFO` block.
    pub fn to_resource(&self) -> Vec<u8> {
        let [major, minor, patch] = self.version.map(u32::from);
        let mut fixed = Vec::new();
        for value in [
            0xFEEF_04BD,           // signature
            0x0001_0000,           // structure version
            (major << 16) | minor, // file version (high)
            patch << 16,           // file version (low)
            (major << 16) | minor, // product version (high)
            patch << 16,           // product version (low)
            0x3F,                  // VS_FFI_FILEFLAGSMASK
            0,                     // file flags
            0x0004_0004,           // VOS_NT_WINDOWS32
            1,                     // VFT_APP
            0,                     // subtype
            0,                     // date (high)
            0,                     // date (low)
        ] {
            push_u32(&mut fixed, value);
        }

        let version = format!("{major}.{minor}.{patch}");
        let strings = [
            ("FileDescription", self.file_description),
            ("ProductName", self.product_name),
            ("FileVersion", version.as_str()),
            ("ProductVersion", version.as_str()),
            ("OriginalFilename", self.original_filename),
        ]
        .map(|(key, value)| version_node(key, NodeValue::Text(value), &[]));
        let table_key = format!("{LANGUAGE:04X}{CODE_PAGE_UNICODE:04X}");
        let table = version_node(&table_key, NodeValue::None, &strings);
        let string_info = version_node("StringFileInfo", NodeValue::None, &[table]);
        let mut translation = Vec::new();
        push_u16(&mut translation, LANGUAGE);
        push_u16(&mut translation, CODE_PAGE_UNICODE);
        let var = version_node("Translation", NodeValue::Binary(&translation), &[]);
        let var_info = version_node("VarFileInfo", NodeValue::None, &[var]);

        version_node("VS_VERSION_INFO", NodeValue::Binary(&fixed), &[string_info, var_info])
    }
}

#[derive(Clone, Copy)]
enum NodeValue<'a> {
    None,
    Text(&'a str),
    Binary(&'a [u8]),
}

/// Serialises one node of the version resource tree (`wLength`, `wValueLength`,
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

/// Target machine of the object file.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Machine {
    /// 32-bit x86.
    X86,
    /// x86-64.
    X64,
    /// 64-bit ARM.
    Arm64,
}

impl Machine {
    /// Maps Cargo's `CARGO_CFG_TARGET_ARCH`.
    pub fn from_arch(arch: &str) -> Option<Self> {
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

const DIRECTORY: usize = 16;
const DIRECTORY_ENTRY: usize = 8;
const DATA_ENTRY: usize = 16;
const SUBDIRECTORY: u32 = 0x8000_0000;

/// Lays out the three-level resource directory (type → name → language), the data
/// entries and the raw data. Returns the section bytes and the offsets of the fields
/// that hold RVAs and therefore need relocations.
fn rsrc_section(resources: &Resources) -> (Vec<u8>, Vec<u32>) {
    let types = &resources.0;
    let name_count: usize = types.values().map(BTreeMap::len).sum();
    let directory = |entries: usize| DIRECTORY + DIRECTORY_ENTRY * entries;

    let names_start = directory(types.len());
    let languages_start = names_start + types.values().map(|n| directory(n.len())).sum::<usize>();
    let data_entries_start = languages_start + name_count * directory(1);
    let data_start = data_entries_start + name_count * DATA_ENTRY;

    let mut out = Vec::new();
    let mut relocations = Vec::new();

    push_directory_header(&mut out, types.len());
    let mut names_offset = names_start;
    for (&kind, names) in types {
        push_u32(&mut out, u32::from(kind));
        push_u32(&mut out, SUBDIRECTORY | to_u32(names_offset));
        names_offset += directory(names.len());
    }

    let mut index = 0;
    for names in types.values() {
        push_directory_header(&mut out, names.len());
        for &id in names.keys() {
            push_u32(&mut out, u32::from(id));
            let language_dir = languages_start + index * directory(1);
            push_u32(&mut out, SUBDIRECTORY | to_u32(language_dir));
            index += 1;
        }
    }

    for index in 0..name_count {
        push_directory_header(&mut out, 1);
        push_u32(&mut out, u32::from(LANGUAGE));
        push_u32(&mut out, to_u32(data_entries_start + index * DATA_ENTRY));
    }

    let mut data_offset = data_start;
    for data in types.values().flat_map(BTreeMap::values) {
        relocations.push(to_u32(out.len()));
        push_u32(&mut out, to_u32(data_offset)); // RVA, completed by the linker
        push_u32(&mut out, to_u32(data.len()));
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
    push_u16(out, u16::try_from(id_entries).expect("entry count fits u16"));
}

const FILE_HEADER: usize = 20;
const SECTION_HEADER: usize = 40;
const RELOCATION: usize = 10;
const SECTION_NAME: [u8; 8] = *b".rsrc\0\0\0";
/// `IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_MEM_READ`.
const SECTION_FLAGS: u32 = 0x4000_0040;

/// Wraps the `.rsrc` section into a relocatable COFF object with one section symbol.
pub fn coff_object(machine: Machine, resources: &Resources) -> Vec<u8> {
    let (section, relocations) = rsrc_section(resources);
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

fn to_u32(value: usize) -> u32 {
    u32::try_from(value).expect("resource object fits u32")
}

fn to_usize(value: u32) -> usize {
    usize::try_from(value).expect("u32 fits usize on Windows targets")
}

fn read_u16(bytes: &[u8], at: usize) -> u16 {
    u16::from_le_bytes([bytes[at], bytes[at + 1]])
}

fn read_u32(bytes: &[u8], at: usize) -> u32 {
    u32::from_le_bytes([bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]])
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

#[cfg(test)]
mod tests {
    use super::*;
    use std::fmt::Write as _;

    /// Size of one COFF symbol-table record.
    const SYMBOL: usize = 18;

    /// A minimal icon file: ICONDIR plus one entry per `(width, data)` image.
    fn ico(images: &[(u8, &[u8])]) -> Vec<u8> {
        let count = u16::try_from(images.len()).unwrap();
        let mut file = Vec::new();
        push_u16(&mut file, 0);
        push_u16(&mut file, 1);
        push_u16(&mut file, count);
        let mut offset = ICONDIR_SIZE + ICONDIRENTRY_SIZE * images.len();
        for &(width, data) in images {
            file.extend_from_slice(&[width, width, 0, 0]);
            push_u16(&mut file, 0); // planes: 0, rc.exe writes 1
            push_u16(&mut file, 32);
            push_u32(&mut file, to_u32(data.len()));
            push_u32(&mut file, to_u32(offset));
            offset += data.len();
        }
        for &(_, data) in images {
            file.extend_from_slice(data);
        }
        file
    }

    #[test]
    fn icon_is_split_into_images_and_group() {
        let mut resources = Resources::default();
        resources.add_icon(1, &ico(&[(16, b"aaaa"), (32, b"bbbbbb")])).unwrap();
        assert_eq!(resources.0[&RT_ICON][&1], b"aaaa");
        assert_eq!(resources.0[&RT_ICON][&2], b"bbbbbb");
        let group = &resources.0[&RT_GROUP_ICON][&1];
        let expected: &[u8] = &[
            0, 0, 1, 0, 2, 0, // GRPICONDIR
            16, 16, 0, 0, 1, 0, 32, 0, 4, 0, 0, 0, 1, 0, // image 1
            32, 32, 0, 0, 1, 0, 32, 0, 6, 0, 0, 0, 2, 0, // image 2
        ];
        assert_eq!(group, expected);
    }

    #[test]
    fn icon_groups_get_distinct_image_ids() {
        let mut resources = Resources::default();
        resources.add_icon(1, &ico(&[(16, b"a"), (32, b"b")])).unwrap();
        resources.add_icon(2, &ico(&[(16, b"c")])).unwrap();
        let ids: Vec<u16> = resources.0[&RT_ICON].keys().copied().collect();
        assert_eq!(ids, [1, 2, 3]);
        assert_eq!(resources.0[&RT_ICON][&3], b"c");
        let second_group = &resources.0[&RT_GROUP_ICON][&2];
        assert_eq!(read_u16(second_group, 6 + 12), 3, "group 2 references image 3");
    }

    #[test]
    fn duplicates_are_rejected() {
        let mut resources = Resources::default();
        resources.add(RT_MANIFEST, 1, b"a".to_vec()).unwrap();
        let error = resources.add(RT_MANIFEST, 1, b"b".to_vec()).unwrap_err();
        assert!(error.contains("duplicate"), "{error}");
        assert_eq!(resources.0[&RT_MANIFEST][&1], b"a", "the first one is kept");
        resources.add_icon(1, &ico(&[(16, b"a")])).unwrap();
        assert!(resources.add_icon(1, &ico(&[(16, b"b")])).is_err());
        assert_eq!(resources.0[&RT_ICON].len(), 1, "nothing added on error");
    }

    #[test]
    fn malformed_icons_are_rejected() {
        let mut resources = Resources::default();
        assert!(resources.add_icon(1, &[0, 0, 1]).is_err(), "short header");
        assert!(resources.add_icon(1, &[0, 0, 2, 0, 1, 0]).is_err(), "cursor type");
        assert!(resources.add_icon(1, &[1, 0, 1, 0, 1, 0]).is_err(), "reserved");
        assert!(resources.add_icon(1, &[0, 0, 1, 0, 0, 0]).is_err(), "no images");
        assert!(resources.add_icon(1, &[0, 0, 1, 0, 1, 0, 16]).is_err(), "cut entry");

        let mut outside = ico(&[(16, b"abcd")]);
        outside.truncate(outside.len() - 1);
        let error = resources.add_icon(1, &outside).unwrap_err();
        assert!(error.contains("outside the file"), "{error}");
        assert!(resources.0.is_empty(), "nothing is added on error");
    }

    #[test]
    fn version_leaf_node_layout() {
        let node = version_node("A", NodeValue::Text("B"), &[]);
        // wLength, wValueLength (chars incl. NUL), wType=text, "A\0", pad, "B\0"
        let expected: &[u8] = &[16, 0, 2, 0, 1, 0, b'A', 0, 0, 0, 0, 0, b'B', 0, 0, 0];
        assert_eq!(node, expected);
    }

    #[test]
    fn version_info_golden() {
        let info = VersionInfo {
            version: [1, 2, 3],
            file_description: "D",
            product_name: "P",
            original_filename: "O.exe",
        };
        let block = info.to_resource();
        let hex = block.iter().fold(String::new(), |mut hex, byte| {
            let _ = write!(hex, "{byte:02x}");
            hex
        });
        // Verified once against `VerQueryValueW` on a linked executable.
        let golden = concat!(
            // VS_VERSION_INFO header: wLength, wValueLength = 52, wType = 0, key
            "bc0134000000560053005f00560045005200530049004f004e005f0049004e0046004f00",
            "00000000",
            // VS_FIXEDFILEINFO: signature, 1.2.3, mask 0x3F, NT_WINDOWS32, VFT_APP
            "bd04effe00000100020001000000030002000100000003003f0000000000000004000400",
            "01000000000000000000000000000000",
            // StringFileInfo header
            "1c010000010053007400720069006e006700460069006c00650049006e0066006f000000",
            // string table 040704B0 header
            "f80000000100300034003000370030003400420030000000",
            // FileDescription = "D"
            "2c0002000100460069006c0065004400650073006300720069007000740069006f006e00",
            "0000000044000000",
            // ProductName = "P"
            "240002000100500072006f0064007500630074004e0061006d0065000000000050000000",
            // FileVersion = "1.2.3"
            "2c0006000100460069006c006500560065007200730069006f006e000000000031002e00",
            "32002e0033000000",
            // ProductVersion = "1.2.3"
            "300006000100500072006f006400750063007400560065007200730069006f006e000000",
            "31002e0032002e0033000000",
            // OriginalFilename = "O.exe"
            "3400060001004f0072006900670069006e0061006c00460069006c0065006e0061006d00",
            "650000004f002e006500780065000000",
            // VarFileInfo header
            "440000000100560061007200460069006c00650049006e0066006f0000000000",
            // Translation = 0x0407, 1200
            "2400040000005400720061006e0073006c006100740069006f006e00000000000704b004",
        );
        assert_eq!(hex, golden);

        // The same facts, spelled out.
        assert_eq!(usize::from(read_u16(&block, 0)), block.len());
        assert_eq!(read_u16(&block, 2), 52, "the fixed info is the root's value");
        assert_eq!(read_u32(&block, 40), 0xFEEF_04BD);
        assert_eq!(read_u32(&block, 48), 0x0001_0002, "file version 1.2");
        assert_eq!(read_u32(&block, 52), 0x0003_0000, "file version .3");
        assert!(block.ends_with(&[0x07, 0x04, 0xB0, 0x04]), "translation 0407/1200");
    }

    #[test]
    fn coff_object_layout_for_one_resource() {
        let mut resources = Resources::default();
        resources.add(RT_MANIFEST, 1, b"xyz".to_vec()).unwrap();
        let object = coff_object(Machine::X64, &resources);

        // Section: root dir (24) + name dir (24) + language dir (24) + data entry (16)
        // + data padded to 8 = 96 bytes.
        let section_size = 96;
        let raw = FILE_HEADER + SECTION_HEADER;
        let relocations = raw + section_size;
        let symbols = relocations + RELOCATION;
        assert_eq!(object.len(), symbols + 2 * SYMBOL + 4);

        assert_eq!(read_u16(&object, 0), 0x8664);
        assert_eq!(read_u16(&object, 2), 1, "one section");
        assert_eq!(to_usize(read_u32(&object, 8)), symbols);
        assert_eq!(read_u32(&object, 12), 2, "symbol + aux record");
        assert_eq!(&object[20..28], b".rsrc\0\0\0");
        assert_eq!(to_usize(read_u32(&object, 36)), section_size);
        assert_eq!(to_usize(read_u32(&object, 40)), raw);
        assert_eq!(to_usize(read_u32(&object, 44)), relocations);
        assert_eq!(read_u16(&object, 52), 1, "one relocation");
        assert_eq!(read_u32(&object, 56), SECTION_FLAGS);

        let section = &object[raw..raw + section_size];
        assert_eq!(read_u16(section, 14), 1, "root: one type");
        assert_eq!(read_u32(section, 16), u32::from(RT_MANIFEST));
        assert_eq!(read_u32(section, 20), SUBDIRECTORY | 0x18);
        assert_eq!(read_u32(section, 24 + 16), 1, "name id");
        assert_eq!(read_u32(section, 24 + 20), SUBDIRECTORY | 0x30);
        assert_eq!(read_u32(section, 48 + 16), u32::from(LANGUAGE));
        assert_eq!(read_u32(section, 48 + 20), 72, "data entry offset");
        assert_eq!(read_u32(section, 72), 88, "data offset (RVA addend)");
        assert_eq!(read_u32(section, 76), 3, "data size");
        assert_eq!(&section[88..91], b"xyz");

        assert_eq!(read_u32(&object, relocations), 72, "relocated field");
        assert_eq!(read_u32(&object, relocations + 4), 0, "section symbol");
        assert_eq!(read_u16(&object, relocations + 8), 3, "IMAGE_REL_AMD64_ADDR32NB");
        assert_eq!(&object[symbols..symbols + 8], b".rsrc\0\0\0");
        assert_eq!(read_u32(&object, object.len() - 4), 4, "empty string table");
    }

    #[test]
    fn coff_entries_are_sorted_and_relocated() {
        let mut resources = Resources::default();
        resources.add(RT_VERSION, 1, vec![1; 9]).unwrap();
        resources.add(RT_ICON, 2, vec![2; 1]).unwrap();
        resources.add(RT_ICON, 1, vec![3; 8]).unwrap();
        let object = coff_object(Machine::Arm64, &resources);
        let section_size = to_usize(read_u32(&object, 36));
        let raw = FILE_HEADER + SECTION_HEADER;
        let section = &object[raw..raw + section_size];

        assert_eq!(read_u16(section, 14), 2, "two types");
        assert_eq!(read_u32(section, 16), u32::from(RT_ICON), "sorted by type");
        assert_eq!(read_u32(section, 24), u32::from(RT_VERSION));
        assert_eq!(read_u16(&object, 52), 3, "one relocation per resource");
        let relocations = raw + section_size;
        for index in 0..3 {
            assert_eq!(read_u16(&object, relocations + index * RELOCATION + 8), 2);
        }
        // Data blobs are 8-byte aligned: 8 + 1 (+7 pad) + 9.
        let data_entries = 16 + 2 * 8 + (16 + 2 * 8) + (16 + 8) + 3 * (16 + 8);
        let first = to_usize(read_u32(section, data_entries));
        let second = to_usize(read_u32(section, data_entries + 16));
        let third = to_usize(read_u32(section, data_entries + 32));
        assert_eq!(second - first, 8);
        assert_eq!(third - second, 8);
        assert_eq!(section_size, third + 16);
    }

    #[test]
    fn machines() {
        assert_eq!(Machine::from_arch("x86_64"), Some(Machine::X64));
        assert_eq!(Machine::from_arch("x86"), Some(Machine::X86));
        assert_eq!(Machine::from_arch("aarch64"), Some(Machine::Arm64));
        assert_eq!(Machine::from_arch("riscv64"), None);
        assert_eq!(Machine::X86.rva_relocation(), 7);
        assert_eq!(Machine::X86.id(), 0x014C);
    }
}
