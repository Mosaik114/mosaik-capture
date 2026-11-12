#![allow(non_snake_case, dead_code)]
use core::ffi::c_void;
pub type H = isize;
pub type WndProc = Option<unsafe extern "system" fn(H, u32, usize, isize) -> isize>;
#[repr(C)] #[derive(Clone, Copy, Default)] pub struct Point { pub x:i32, pub y:i32 }
#[repr(C)] #[derive(Clone, Copy, Default)] pub struct Rect { pub left:i32,pub top:i32,pub right:i32,pub bottom:i32 }
#[repr(C)] pub struct WndClass { pub style:u32,pub proc:WndProc,pub cls_extra:i32,pub wnd_extra:i32,pub instance:H,pub icon:H,pub cursor:H,pub background:H,pub menu:*const u16,pub name:*const u16 }
#[repr(C)] pub struct Msg { pub hwnd:H,pub message:u32,pub w:usize,pub l:isize,pub time:u32,pub point:Point,pub private:u32 }
#[repr(C)] pub struct Paint { pub dc:H,pub erase:i32,pub rect:Rect,pub restore:i32,pub update:i32,pub reserved:[u8;32] }
#[repr(C)] pub struct DrawItem { pub kind:u32,pub id:u32,pub item:u32,pub action:u32,pub state:u32,pub hwnd:H,pub dc:H,pub rect:Rect,pub data:usize }
#[repr(C)] pub struct NotifyIcon { pub size:u32,pub hwnd:H,pub id:u32,pub flags:u32,pub callback:u32,pub icon:H,pub tip:[u16;128],pub state:u32,pub state_mask:u32,pub info:[u16;256],pub version:u32,pub info_title:[u16;64],pub info_flags:u32,pub guid:[u8;16],pub balloon:H }
#[repr(C)] pub struct BrowseInfo { pub owner:H,pub root:*mut c_void,pub display:*mut u16,pub title:*const u16,pub flags:u32,pub callback:Option<unsafe extern "system" fn(H,u32,isize,isize)->i32>,pub param:isize,pub image:i32 }
#[repr(C)] pub struct StartupInfo { pub size:u32,pub reserved:*mut u16,pub desktop:*mut u16,pub title:*mut u16,pub x:u32,pub y:u32,pub width:u32,pub height:u32,pub chars_x:u32,pub chars_y:u32,pub fill:u32,pub flags:u32,pub show:u16,pub reserved_size:u16,pub reserved_data:*mut u8,pub stdin:H,pub stdout:H,pub stderr:H }
#[repr(C)] pub struct ProcessInfo { pub process:H,pub thread:H,pub pid:u32,pub tid:u32 }

#[link(name="kernel32")]
unsafe extern "system" {
 pub fn ExitProcess(code:u32)->!;
 pub fn GetModuleHandleW(name:*const u16)->H;
 pub fn GetModuleFileNameW(module:H,path:*mut u16,size:u32)->u32;
 pub fn GetEnvironmentVariableW(name:*const u16,value:*mut u16,size:u32)->u32;
 pub fn SetEnvironmentVariableW(name:*const u16,value:*const u16)->i32;
 pub fn GetCommandLineW()->*const u16;
 pub fn GetLastError()->u32;
 pub fn CreateMutexW(attrs:*const c_void,owner:i32,name:*const u16)->H;
 pub fn CloseHandle(handle:H)->i32;
 pub fn LocalFree(memory:H)->H;
 pub fn Sleep(ms:u32);
 pub fn CreateDirectoryW(path:*const u16,attrs:*const c_void)->i32;
 pub fn GetPrivateProfileStringW(section:*const u16,key:*const u16,fallback:*const u16,value:*mut u16,size:u32,path:*const u16)->u32;
 pub fn GetPrivateProfileIntW(section:*const u16,key:*const u16,fallback:i32,path:*const u16)->u32;
 pub fn WritePrivateProfileStringW(section:*const u16,key:*const u16,value:*const u16,path:*const u16)->i32;
 pub fn MoveFileExW(old:*const u16,new:*const u16,flags:u32)->i32;
 pub fn CreateProcessW(app:*const u16,cmd:*mut u16,pa:*const c_void,ta:*const c_void,inherit:i32,flags:u32,env:*const c_void,dir:*const u16,start:*mut StartupInfo,info:*mut ProcessInfo)->i32;
 pub fn CreateFileW(path:*const u16,access:u32,share:u32,attrs:*const c_void,creation:u32,flags:u32,template:H)->H;
 pub fn WriteFile(file:H,buffer:*const u8,size:u32,written:*mut u32,overlapped:*mut c_void)->i32;
}
#[link(name="user32")]
unsafe extern "system" {
 pub fn RegisterClassW(class:*const WndClass)->u16;
 pub fn CreateWindowExW(ex:u32,class:*const u16,title:*const u16,style:u32,x:i32,y:i32,width:i32,height:i32,parent:H,menu:H,instance:H,param:*const c_void)->H;
 pub fn DefWindowProcW(hwnd:H,msg:u32,w:usize,l:isize)->isize;
 pub fn DestroyWindow(hwnd:H)->i32;
 pub fn GetMessageW(msg:*mut Msg,hwnd:H,min:u32,max:u32)->i32;
 pub fn TranslateMessage(msg:*const Msg)->i32;
 pub fn DispatchMessageW(msg:*const Msg)->isize;
 pub fn IsDialogMessageW(hwnd:H,msg:*mut Msg)->i32;
 pub fn PostQuitMessage(code:i32);
 pub fn ShowWindow(hwnd:H,command:i32)->i32;
 pub fn SetForegroundWindow(hwnd:H)->i32;
 pub fn AllowSetForegroundWindow(pid:u32)->i32;
 pub fn GetWindowThreadProcessId(hwnd:H,pid:*mut u32)->u32;
 pub fn SetFocus(hwnd:H)->H;
 pub fn SetWindowTextW(hwnd:H,text:*const u16)->i32;
 pub fn GetWindowTextW(hwnd:H,text:*mut u16,size:i32)->i32;
 pub fn FindWindowW(class:*const u16,title:*const u16)->H;
 pub fn RegisterHotKey(hwnd:H,id:i32,mods:u32,key:u32)->i32;
 pub fn UnregisterHotKey(hwnd:H,id:i32)->i32;
 pub fn SendMessageW(hwnd:H,msg:u32,w:usize,l:isize)->isize;
 pub fn SendMessageTimeoutW(hwnd:H,msg:u32,w:usize,l:isize,flags:u32,timeout:u32,result:*mut usize)->isize;
 pub fn PostMessageW(hwnd:H,msg:u32,w:usize,l:isize)->i32;
 pub fn GetDlgItem(hwnd:H,id:i32)->H;
 pub fn LoadIconW(instance:H,name:*const u16)->H;
 pub fn LoadCursorW(instance:H,name:*const u16)->H;
 pub fn SetProcessDpiAwarenessContext(context:H)->i32;
 pub fn GetDpiForSystem()->u32;
 pub fn AdjustWindowRectEx(rect:*mut Rect,style:u32,menu:i32,ex:u32)->i32;
 pub fn SystemParametersInfoW(action:u32,param:u32,value:*mut c_void,flags:u32)->i32;
 pub fn MessageBoxW(hwnd:H,text:*const u16,title:*const u16,kind:u32)->i32;
 pub fn BeginPaint(hwnd:H,paint:*mut Paint)->H;
 pub fn EndPaint(hwnd:H,paint:*const Paint)->i32;
 pub fn FillRect(dc:H,rect:*const Rect,brush:H)->i32;
 pub fn DrawTextW(dc:H,text:*const u16,len:i32,rect:*mut Rect,format:u32)->i32;
 pub fn InvalidateRect(hwnd:H,rect:*const Rect,erase:i32)->i32;
 pub fn CreatePopupMenu()->H;
 pub fn AppendMenuW(menu:H,flags:u32,id:usize,text:*const u16)->i32;
 pub fn TrackPopupMenu(menu:H,flags:u32,x:i32,y:i32,reserved:i32,hwnd:H,rect:*const Rect)->i32;
 pub fn DestroyMenu(menu:H)->i32;
 pub fn GetCursorPos(point:*mut Point)->i32;
}
#[link(name="gdi32")]
unsafe extern "system" {
 pub fn CreateSolidBrush(color:u32)->H;
 pub fn CreateFontW(height:i32,width:i32,esc:i32,orientation:i32,weight:i32,italic:u32,underline:u32,strike:u32,charset:u32,out:u32,clip:u32,quality:u32,family:u32,face:*const u16)->H;
 pub fn DeleteObject(object:H)->i32;
 pub fn SetTextColor(dc:H,color:u32)->u32;
 pub fn SetBkColor(dc:H,color:u32)->u32;
 pub fn SetBkMode(dc:H,mode:i32)->i32;
 pub fn SelectObject(dc:H,object:H)->H;
 pub fn RoundRect(dc:H,left:i32,top:i32,right:i32,bottom:i32,cx:i32,cy:i32)->i32;
 pub fn CreatePen(style:i32,width:i32,color:u32)->H;
}
#[link(name="shell32")]
unsafe extern "system" {
 pub fn Shell_NotifyIconW(action:u32,info:*const NotifyIcon)->i32;
 pub fn ShellExecuteW(hwnd:H,verb:*const u16,file:*const u16,args:*const u16,dir:*const u16,show:i32)->H;
 pub fn CommandLineToArgvW(cmd:*const u16,count:*mut i32)->*mut *mut u16;
 pub fn SHGetFolderPathW(hwnd:H,folder:i32,token:H,flags:u32,path:*mut u16)->i32;
 pub fn SHBrowseForFolderW(info:*const BrowseInfo)->*mut c_void;
 pub fn SHGetPathFromIDListW(id:*const c_void,path:*mut u16)->i32;
}
#[link(name="ole32")]
unsafe extern "system" { pub fn CoInitializeEx(reserved:*const c_void,flags:u32)->i32; pub fn CoUninitialize(); pub fn CoTaskMemFree(memory:*const c_void); }
#[link(name="advapi32")]
unsafe extern "system" {
 pub fn RegCreateKeyExW(key:H,path:*const u16,reserved:u32,class:*const u16,options:u32,access:u32,security:*const c_void,result:*mut H,disposition:*mut u32)->i32;
 pub fn RegSetValueExW(key:H,name:*const u16,reserved:u32,kind:u32,data:*const u8,size:u32)->i32;
 pub fn RegDeleteValueW(key:H,name:*const u16)->i32;
 pub fn RegCloseKey(key:H)->i32;
}
#[link(name="dwmapi")]
unsafe extern "system" { pub fn DwmSetWindowAttribute(hwnd:H,attribute:u32,value:*const c_void,size:u32)->i32; }
#[link(name="uxtheme")]
unsafe extern "system" { pub fn SetWindowTheme(hwnd:H,app:*const u16,id:*const u16)->i32; }
