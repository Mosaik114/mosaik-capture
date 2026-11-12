//! The shell's folder picker (`IFileOpenDialog` with `FOS_PICKFOLDERS`), which, unlike
//! the old `SHBrowseForFolder`, supports long paths.
//!
//! `windows-sys` has no COM interface definitions, so the two interfaces used here are
//! declared as minimal vtables: only the called methods are typed, the slots in between
//! are pointer-sized placeholders. Their positions are checked by unit tests.

use std::ffi::c_void;
use std::marker::PhantomData;
use std::path::{Path, PathBuf};
use std::ptr::{self, NonNull};

use windows_sys::Win32::Foundation::HWND;
use windows_sys::Win32::System::Com::{CLSCTX_INPROC_SERVER, CoCreateInstance, CoTaskMemFree};
use windows_sys::Win32::UI::Shell::{
    FOS_FORCEFILESYSTEM, FOS_PICKFOLDERS, FileOpenDialog, SHCreateItemFromParsingName,
    SIGDN_FILESYSPATH,
};
use windows_sys::core::{GUID, HRESULT, PCWSTR, PWSTR};

use super::strings;
use crate::wide::{from_wide_ptr, to_wide};

/// `IID_IFileOpenDialog`.
const IID_FILE_OPEN_DIALOG: GUID = GUID::from_u128(0xd57c_7288_d4ad_4768_be02_9d96_9532_d960);
/// `IID_IShellItem`.
const IID_SHELL_ITEM: GUID = GUID::from_u128(0x4382_6d1e_e718_42ee_bc55_a1e2_61c3_7bfe);

/// An unused vtable slot.
type Slot = usize;
/// The interface pointer passed as first argument of every method.
type This = *mut c_void;

#[repr(C)]
struct UnknownVtbl {
    // HRESULT QueryInterface(REFIID riid, void **ppvObject);
    query_interface: Slot,
    // ULONG AddRef(void);
    add_ref: Slot,
    // ULONG Release(void);
    release: unsafe extern "system" fn(This) -> u32,
}

/// `IFileOpenDialog` up to `GetResult` (`IUnknown` → `IModalWindow` → `IFileDialog`).
#[repr(C)]
struct FileDialogVtbl {
    unknown: UnknownVtbl,
    // IModalWindow: HRESULT Show(HWND hwndOwner);
    show: unsafe extern "system" fn(This, HWND) -> HRESULT,
    // HRESULT SetFileTypes(UINT cFileTypes, const COMDLG_FILTERSPEC *rgFilterSpec);
    set_file_types: Slot,
    // HRESULT SetFileTypeIndex(UINT iFileType);
    set_file_type_index: Slot,
    // HRESULT GetFileTypeIndex(UINT *piFileType);
    get_file_type_index: Slot,
    // HRESULT Advise(IFileDialogEvents *pfde, DWORD *pdwCookie);
    advise: Slot,
    // HRESULT Unadvise(DWORD dwCookie);
    unadvise: Slot,
    // HRESULT SetOptions(FILEOPENDIALOGOPTIONS fos);
    set_options: unsafe extern "system" fn(This, u32) -> HRESULT,
    // HRESULT GetOptions(FILEOPENDIALOGOPTIONS *pfos);
    get_options: unsafe extern "system" fn(This, *mut u32) -> HRESULT,
    // HRESULT SetDefaultFolder(IShellItem *psi);
    set_default_folder: Slot,
    // HRESULT SetFolder(IShellItem *psi);
    set_folder: unsafe extern "system" fn(This, This) -> HRESULT,
    // HRESULT GetFolder(IShellItem **ppsi);
    get_folder: Slot,
    // HRESULT GetCurrentSelection(IShellItem **ppsi);
    get_current_selection: Slot,
    // HRESULT SetFileName(LPCWSTR pszName);
    set_file_name: Slot,
    // HRESULT GetFileName(LPWSTR *pszName);
    get_file_name: Slot,
    // HRESULT SetTitle(LPCWSTR pszTitle);
    set_title: unsafe extern "system" fn(This, PCWSTR) -> HRESULT,
    // HRESULT SetOkButtonLabel(LPCWSTR pszText);
    set_ok_button_label: Slot,
    // HRESULT SetFileNameLabel(LPCWSTR pszLabel);
    set_file_name_label: Slot,
    // HRESULT GetResult(IShellItem **ppsi);
    get_result: unsafe extern "system" fn(This, *mut This) -> HRESULT,
}

/// `IShellItem` up to `GetDisplayName`.
#[repr(C)]
struct ShellItemVtbl {
    unknown: UnknownVtbl,
    // HRESULT BindToHandler(IBindCtx *pbc, REFGUID bhid, REFIID riid, void **ppv);
    bind_to_handler: Slot,
    // HRESULT GetParent(IShellItem **ppsi);
    get_parent: Slot,
    // HRESULT GetDisplayName(SIGDN sigdnName, LPWSTR *ppszName);
    get_display_name: unsafe extern "system" fn(This, i32, *mut PWSTR) -> HRESULT,
}

/// Vtables that start with `IUnknown`.
trait Vtbl {
    fn unknown(&self) -> &UnknownVtbl;
}

impl Vtbl for FileDialogVtbl {
    fn unknown(&self) -> &UnknownVtbl {
        &self.unknown
    }
}

impl Vtbl for ShellItemVtbl {
    fn unknown(&self) -> &UnknownVtbl {
        &self.unknown
    }
}

/// An owned COM interface pointer, released on drop.
struct Com<V: Vtbl> {
    this: NonNull<c_void>,
    vtbl: PhantomData<V>,
}

impl<V: Vtbl> Com<V> {
    /// Takes ownership of `raw` if the call that produced it succeeded. After a failed
    /// call, `raw` is not touched (COM leaves it null or undefined, and it must not be
    /// released).
    ///
    /// # Safety
    ///
    /// If `result` signals success, `raw` must be null or an owned reference to an
    /// interface whose vtable is `V`. Nothing is required after a failure.
    unsafe fn from_result(result: HRESULT, raw: This) -> Option<Self> {
        if result < 0 {
            return None;
        }
        NonNull::new(raw).map(|this| Self { this, vtbl: PhantomData })
    }

    fn this(&self) -> This {
        self.this.as_ptr()
    }

    fn vtbl(&self) -> &V {
        // SAFETY: a COM object starts with a pointer to its vtable, which lives at least
        // as long as the object (see `from_result`).
        unsafe { &**self.this.as_ptr().cast::<*const V>() }
    }
}

impl<V: Vtbl> Drop for Com<V> {
    fn drop(&mut self) {
        // SAFETY: we own one reference, released exactly once.
        unsafe { (self.vtbl().unknown().release)(self.this()) };
    }
}

/// Lets the user pick a folder, starting in `initial` if it exists. `None` if the
/// dialog was cancelled or could not be shown. COM must be initialised on this thread.
pub fn pick_folder(owner: HWND, initial: &Path) -> Option<PathBuf> {
    let dialog = create_dialog()?;
    let vtbl = dialog.vtbl();
    let title = to_wide(strings::PICK_FOLDER_TITLE);
    let mut options = 0;
    // SAFETY: `dialog` is a live IFileOpenDialog; all pointers outlive the calls.
    unsafe {
        // Keep the dialog's defaults if they can be read; without FOS_PICKFOLDERS the
        // dialog would pick files, so failing to set it means not showing it at all.
        if (vtbl.get_options)(dialog.this(), &raw mut options) < 0 {
            options = 0;
        }
        let picks_folders =
            (vtbl.set_options)(dialog.this(), options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
        if picks_folders < 0 {
            return None;
        }
        (vtbl.set_title)(dialog.this(), title.as_ptr());
        if let Some(folder) = shell_item(initial) {
            (vtbl.set_folder)(dialog.this(), folder.this());
        }
    }
    // SAFETY: modal call; returns a failure HRESULT when cancelled.
    if unsafe { (vtbl.show)(dialog.this(), owner) } < 0 {
        return None;
    }
    let mut raw = ptr::null_mut();
    // SAFETY: `raw` receives an owned IShellItem on success.
    let result = unsafe { (vtbl.get_result)(dialog.this(), &raw mut raw) };
    // SAFETY: GetResult hands out an IShellItem reference.
    let item = unsafe { Com::<ShellItemVtbl>::from_result(result, raw) }?;
    file_system_path(&item)
}

fn create_dialog() -> Option<Com<FileDialogVtbl>> {
    let mut raw = ptr::null_mut();
    // SAFETY: standard COM activation; `raw` receives the requested interface.
    let result = unsafe {
        CoCreateInstance(
            &FileOpenDialog,
            ptr::null_mut(),
            CLSCTX_INPROC_SERVER,
            &IID_FILE_OPEN_DIALOG,
            &raw mut raw,
        )
    };
    // SAFETY: the requested interface is IFileOpenDialog.
    unsafe { Com::from_result(result, raw) }
}

/// The shell item for an existing folder path.
fn shell_item(path: &Path) -> Option<Com<ShellItemVtbl>> {
    if path.as_os_str().is_empty() {
        return None;
    }
    let path = to_wide(path);
    let mut raw = ptr::null_mut();
    // SAFETY: `path` is NUL-terminated; `raw` receives the requested interface.
    let result = unsafe {
        SHCreateItemFromParsingName(path.as_ptr(), ptr::null_mut(), &IID_SHELL_ITEM, &raw mut raw)
    };
    // SAFETY: the requested interface is IShellItem.
    unsafe { Com::from_result(result, raw) }
}

fn file_system_path(item: &Com<ShellItemVtbl>) -> Option<PathBuf> {
    let mut name: PWSTR = ptr::null_mut();
    // SAFETY: `name` receives a COM-allocated string on success.
    let result =
        unsafe { (item.vtbl().get_display_name)(item.this(), SIGDN_FILESYSPATH, &raw mut name) };
    let path = (result >= 0).then(|| {
        // SAFETY: on success `name` is a NUL-terminated string owned by us.
        PathBuf::from(unsafe { from_wide_ptr(name) })
    });
    // SAFETY: allocated by the shell with the COM task allocator (null is allowed).
    unsafe { CoTaskMemFree(name.cast()) };
    path
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::{offset_of, size_of};
    use windows_sys::Win32::System::Com::{
        COINIT_APARTMENTTHREADED, CoInitializeEx, CoUninitialize,
    };

    fn slot(index: usize) -> usize {
        index * size_of::<usize>()
    }

    #[test]
    fn file_dialog_vtable_matches_shobjidl() {
        assert_eq!(offset_of!(FileDialogVtbl, show), slot(3));
        assert_eq!(offset_of!(FileDialogVtbl, set_options), slot(9));
        assert_eq!(offset_of!(FileDialogVtbl, get_options), slot(10));
        assert_eq!(offset_of!(FileDialogVtbl, set_folder), slot(12));
        assert_eq!(offset_of!(FileDialogVtbl, set_title), slot(17));
        assert_eq!(offset_of!(FileDialogVtbl, get_result), slot(20));
    }

    #[test]
    fn shell_item_vtable_matches_shobjidl() {
        assert_eq!(offset_of!(UnknownVtbl, release), slot(2));
        assert_eq!(offset_of!(ShellItemVtbl, get_display_name), slot(5));
    }

    #[test]
    fn shell_items_for_existing_folders_only() {
        // SAFETY: balanced below; the test thread is ours.
        let initialised =
            unsafe { CoInitializeEx(ptr::null(), COINIT_APARTMENTTHREADED.cast_unsigned()) } >= 0;
        {
            let temp = std::env::temp_dir();
            let item = shell_item(&temp).expect("temp folder exists");
            let path = file_system_path(&item).unwrap();
            assert!(path.is_dir(), "{}", path.display());
            assert!(shell_item(Path::new("")).is_none());
            assert!(shell_item(Path::new(r"Z:\definitely\not\here\9f1c")).is_none());
        }
        if initialised {
            // SAFETY: balances the successful CoInitializeEx above.
            unsafe { CoUninitialize() };
        }
    }
}
