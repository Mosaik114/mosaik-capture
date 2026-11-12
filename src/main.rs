#![no_std]
#![no_main]
#![allow(unsafe_op_in_unsafe_fn, static_mut_refs, non_snake_case)]
mod win;
mod config;
use config::{Config,Text,parse_key};
use win::*;
use core::{mem,ptr,panic::PanicInfo,ffi::c_void};

const CLASS:&str="MosaiksAufnahmeRust";
const TITLE:&str="Mosaiks Aufnahme – Einstellungen";
const WM_APP:u32=0x8000;
const BG:u32=0x242626;
const FIELD:u32=0x2f3435;
const INK:u32=0xf5f9fa;
const ACCENT:u32=0x5777d9;
static mut APP:H=0;
static mut INSTANCE:H=0;
static mut CFG:Config=Config::empty();
static mut DRAFT:Config=Config::empty();
static mut FONT:H=0;
static mut BIG:H=0;
static mut BOLD:H=0;
static mut BRUSH:H=0;
static mut EDIT_BRUSH:H=0;
static mut SCALE:i32=96;
static mut KEYS:u32=0;
static mut STATUS_OK:bool=true;
static mut TASKBAR_MESSAGE:u32=0;
static mut PREVIEW:bool=false;

#[link(name="user32")]
unsafe extern "system" {fn RegisterWindowMessageW(name:*const u16)->u32;}
// Vorhandene Windows-/VC-Laufzeitfunktionen, ohne zusaetzliche SDK-Importbibliotheken.
#[link(name="kernelbase",kind="raw-dylib")]
unsafe extern "C" { fn __chkstk(); }
#[link(name="vcruntime140",kind="raw-dylib")]
unsafe extern "C" { fn __CxxFrameHandler3(a:*mut c_void,b:*mut c_void,c:*mut c_void,d:*mut c_void)->i32; }
#[unsafe(no_mangle)] pub static _fltused:i32=0;

#[panic_handler] fn panic(_: &PanicInfo)->! {unsafe{ExitProcess(2)}}
#[unsafe(no_mangle)] pub unsafe extern "C" fn memcpy(dst:*mut c_void,src:*const c_void,n:usize)->*mut c_void {for i in 0..n{(dst as*mut u8).add(i).write_volatile((src as*const u8).add(i).read_volatile());}dst}
#[unsafe(no_mangle)] pub unsafe extern "C" fn memset(dst:*mut c_void,value:i32,n:usize)->*mut c_void {for i in 0..n{(dst as*mut u8).add(i).write_volatile(value as u8);}dst}
#[unsafe(no_mangle)] pub unsafe extern "C" fn memmove(dst:*mut c_void,src:*const c_void,n:usize)->*mut c_void {if(dst as usize)<(src as usize){memcpy(dst,src,n);}else{for i in(0..n).rev(){(dst as*mut u8).add(i).write_volatile((src as*const u8).add(i).read_volatile());}}dst}
#[unsafe(no_mangle)] pub unsafe extern "C" fn memcmp(a:*const c_void,b:*const c_void,n:usize)->i32 {for i in 0..n{let x=(a as*const u8).add(i).read_volatile();let y=(b as*const u8).add(i).read_volatile();if x!=y{return x as i32-y as i32;}}0}

unsafe fn px(n:i32)->i32 {(n*SCALE+48)/96}
unsafe fn item(id:i32)->H{GetDlgItem(APP,id)}
unsafe fn set(id:i32,text:&Text){SetWindowTextW(item(id),text.p());}
unsafe fn get(id:i32)->Text{let mut t=Text::empty();GetWindowTextW(item(id),t.buf.as_mut_ptr(),512);t}
unsafe fn check(id:i32,on:bool){SendMessageW(item(id),0xf1,on as usize,0);}
unsafe fn checked(id:i32)->bool{SendMessageW(item(id),0xf0,0,0)==1}
unsafe fn status(text:&str,ok:bool){STATUS_OK=ok;set(92,&Text::of(text));InvalidateRect(item(92),ptr::null(),1);}
unsafe fn control(class:&str,text:&str,id:i32,style:u32,x:i32,y:i32,w:i32,h:i32)->H{
    let c=CreateWindowExW(if class=="EDIT"{0x200}else{0},Text::of(class).p(),Text::of(text).p(),0x50000000|style,px(x),px(y),px(w),px(h),APP,id as H,INSTANCE,ptr::null());
    SendMessageW(c,0x30,FONT as usize,1);
    if class=="EDIT"{SendMessageW(c,0xc5,511,0);}
    c
}
unsafe fn label(text:&str,x:i32,y:i32,w:i32,h:i32,font:H){let c=control("STATIC",text,0,0,x,y,w,h);SendMessageW(c,0x30,font as usize,1);}
unsafe fn button(text:&str,id:i32,x:i32,y:i32,w:i32,h:i32){control("BUTTON",text,id,0x10000|0xb,x,y,w,h);}
unsafe fn edit(id:i32,x:i32,y:i32,w:i32){control("EDIT","",id,0x10000|0x80,x,y,w,30);}
unsafe fn checkbox(text:&str,id:i32,x:i32,y:i32,w:i32){let c=control("BUTTON",text,id,0x10000|3,x,y,w,28);let empty=Text::empty();SetWindowTheme(c,empty.p(),empty.p());}
unsafe fn combo(id:i32,x:i32,y:i32,w:i32,values:&[&str]){
    let c=control("COMBOBOX","",id,0x10000|0x200000|3,x,y,w,140);
    for value in values{SendMessageW(c,0x143,0,Text::of(value).p()as isize);}
}
unsafe fn populate(){
    set(10,&CFG.screenshot_key);set(11,&CFG.screenshot_folder);set(20,&CFG.recording_key);set(21,&CFG.recording_folder);
    check(25,CFG.microphone);check(26,CFG.audio);check(27,CFG.clicks);check(30,CFG.autostart);
    SendMessageW(item(24),0x14e,if CFG.fps==30{0}else{1},0);
    SendMessageW(item(29),0x14e,match CFG.countdown{0=>0,5=>2,_=>1},0);
}
unsafe fn init_ui(){
    let face=Text::of("Segoe UI");
    FONT=CreateFontW(-px(15),0,0,0,400,0,0,0,1,0,0,5,0,face.p());
    BIG=CreateFontW(-px(27),0,0,0,600,0,0,0,1,0,0,5,0,face.p());
    BOLD=CreateFontW(-px(19),0,0,0,600,0,0,0,1,0,0,5,0,face.p());
    label("Festhalten, was zählt.",28,22,670,36,BIG);
    label("Screenshots und Bildschirmaufnahmen",29,63,650,25,FONT);
    label("Screenshot",28,112,650,28,BOLD);
    label("Speicherort",28,151,600,22,FONT);
    edit(11,28,176,532);button("Auswählen",12,572,176,130,30);
    label("Tastenkürzel",28,225,140,24,FONT);edit(10,170,220,200);
    button("Screenshot erstellen",100,404,220,298,34);
    label("Bildschirmaufnahme",28,285,650,28,BOLD);
    label("Speicherort",28,324,600,22,FONT);
    edit(21,28,349,532);button("Auswählen",22,572,349,130,30);
    label("Tastenkürzel",28,398,140,24,FONT);edit(20,170,393,200);
    button("Aufnahme starten / beenden",101,404,393,298,34);
    checkbox("Mikrofon",25,28,445,170);checkbox("Systemton",26,220,445,170);checkbox("Mausklicks anzeigen",27,412,445,290);
    label("Bildrate",28,490,85,25,FONT);combo(24,115,485,135,&["30 Bilder/s","60 Bilder/s"]);
    label("Vorlauf",285,490,90,25,FONT);combo(29,375,485,150,&["Ohne Vorlauf","3 Sekunden","5 Sekunden"]);
    checkbox("Mit Windows starten",30,28,534,380);
    button("Speichern",90,28,583,190,38);button("Screenshot-Ordner",13,232,583,220,38);button("Aufnahme-Ordner",23,466,583,236,38);
    control("STATIC","Bereit.",92,0,28,636,675,36);
    populate();
}
unsafe fn show_settings(recording:bool){
    populate();ShowWindow(APP,9);SetForegroundWindow(APP);SetFocus(item(if recording{20}else{10}));
}
unsafe fn register(c:&Config)->bool {
    let a=parse_key(&c.screenshot_key);let b=parse_key(&c.recording_key);
    let(Some(a),Some(b))=(a,b)else{return false;};if a==b{return false;}
    KEYS=0;
    if RegisterHotKey(APP,1,a.modifiers|0x4000,a.code)!=0{KEYS|=2;}
    if RegisterHotKey(APP,2,b.modifiers|0x4000,b.code)!=0{KEYS|=4;}
    KEYS==6
}
unsafe fn save(){
    DRAFT=CFG;DRAFT.screenshot_key=get(10);DRAFT.recording_key=get(20);
    let a=parse_key(&DRAFT.screenshot_key);let b=parse_key(&DRAFT.recording_key);
    if a.is_none()||b.is_none(){status("Bitte ein gültiges Kürzel eingeben, etwa Shift+Alt+4.",false);return;}
    if a==b{status("Screenshot und Aufnahme brauchen unterschiedliche Kürzel.",false);return;}
    DRAFT.screenshot_folder=get(11);DRAFT.recording_folder=get(21);
    if !config::valid_folder(&DRAFT.screenshot_folder)||!config::valid_folder(&DRAFT.recording_folder){status("Bitte vollständige, gültige Ordnerpfade auswählen.",false);return;}
    DRAFT.microphone=checked(25);DRAFT.audio=checked(26);DRAFT.clicks=checked(27);DRAFT.autostart=checked(30);
    DRAFT.fps=if SendMessageW(item(24),0x147,0,0)==0{30}else{60};
    DRAFT.countdown=match SendMessageW(item(29),0x147,0,0){0=>0,2=>5,_=>3};
    UnregisterHotKey(APP,1);UnregisterHotKey(APP,2);
    if !register(&DRAFT){UnregisterHotKey(APP,1);UnregisterHotKey(APP,2);register(&CFG);status("Ein Kürzel ist bereits belegt. Die bisherigen Kürzel bleiben aktiv.",false);return;}
    if (!PREVIEW&&!config::login(DRAFT.autostart))||!config::persist(&DRAFT){
        UnregisterHotKey(APP,1);UnregisterHotKey(APP,2);register(&CFG);if !PREVIEW{config::login(CFG.autostart);}
        status("Die Einstellungen konnten nicht gespeichert werden.",false);return;
    }
    CFG=DRAFT;status("Gespeichert. Beide Tastenkürzel sind aktiv.",true);
}
unsafe extern "system" fn browse_callback(hwnd:H,message:u32,_:isize,path:isize)->i32{if message==1{SendMessageW(hwnd,0x467,1,path);}0}
unsafe fn browse(id:i32){
    let current=get(id);let mut display=Text::empty();
    let info=BrowseInfo{owner:APP,root:ptr::null_mut(),display:display.buf.as_mut_ptr(),title:ptr::null(),flags:1|0x40,callback:Some(browse_callback),param:current.p()as isize,image:0};
    // Der Titelpuffer muss waehrend des modalen Dialogs erhalten bleiben.
    let title=Text::of("Speicherordner auswählen");let info=BrowseInfo{title:title.p(),..info};
    let folder=SHBrowseForFolderW(&info);
    if !folder.is_null(){let mut value=Text::empty();if SHGetPathFromIDListW(folder,value.buf.as_mut_ptr())!=0{set(id,&value);}CoTaskMemFree(folder);}
}
unsafe fn open_folder(recording:bool){
    let folder=if recording{CFG.recording_folder}else{CFG.screenshot_folder};
    ShellExecuteW(APP,Text::of("open").p(),folder.p(),ptr::null(),ptr::null(),1);
}
unsafe fn engine()->H{FindWindowW(ptr::null(),Text::of("Mosaiks Capture Engine").p())}
unsafe fn engine_state()->usize{let window=engine();let mut state=0;if window!=0{SendMessageTimeoutW(window,WM_APP+10,0,0,2,400,&mut state);}state}
unsafe fn capture(recording:bool){
    ShowWindow(APP,0);
    let window=engine();
    let ok=if window!=0{PostMessageW(window,WM_APP+if recording{2}else{1},0,0)!=0}else{config::spawn_engine(recording)};
    if !ok{show_settings(recording);status("Das Aufnahmeprogramm konnte nicht gestartet werden.",false);}
}
unsafe fn tray(action:u32){
    let mut info:NotifyIcon=mem::zeroed();info.size=mem::size_of::<NotifyIcon>()as u32;info.hwnd=APP;info.id=1;info.flags=7;info.callback=WM_APP+30;
    info.icon=LoadIconW(INSTANCE,1usize as *const u16);
    let tip=Text::of("Mosaiks Aufnahme – Screenshot und Bildschirmaufnahme");info.tip[..tip.len()].copy_from_slice(&tip.buf[..tip.len()]);
    Shell_NotifyIconW(action,&info);
}
unsafe fn quit(){
    if engine_state()&1!=0{show_settings(true);status("Bitte die laufende Aufnahme oder Bereichsauswahl zuerst beenden.",false);return;}
    let window=engine();if window!=0{PostMessageW(window,WM_APP+3,0,0);}DestroyWindow(APP);
}
unsafe fn menu(){
    let popup=CreatePopupMenu();
    for(id,text)in[(100,"Screenshot erstellen"),(101,"Aufnahme starten / beenden"),(200,"Einstellungen"),(201,"Beenden")]{AppendMenuW(popup,0,id,Text::of(text).p());}
    let mut point=Point::default();GetCursorPos(&mut point);SetForegroundWindow(APP);
    let chosen=TrackPopupMenu(popup,0x100|2,point.x,point.y,0,APP,ptr::null());DestroyMenu(popup);PostMessageW(APP,0,0,0);
    match chosen{100=>capture(false),101=>capture(true),200=>show_settings(false),201=>quit(),_=>{}}
}
unsafe fn draw_button(draw:&DrawItem){
    let accent=draw.id==90||draw.id==100||draw.id==101;
    let brush=CreateSolidBrush(if accent{ACCENT}else{FIELD});let pen=CreatePen(0,1,if accent{ACCENT}else{0x454747});
    let old_brush=SelectObject(draw.dc,brush);let old_pen=SelectObject(draw.dc,pen);
    RoundRect(draw.dc,draw.rect.left,draw.rect.top,draw.rect.right,draw.rect.bottom,px(10),px(10));
    SelectObject(draw.dc,old_brush);SelectObject(draw.dc,old_pen);DeleteObject(brush);DeleteObject(pen);
    let mut text=Text::empty();GetWindowTextW(draw.hwnd,text.buf.as_mut_ptr(),512);SetBkMode(draw.dc,1);SetTextColor(draw.dc,if accent{0x131414}else{INK});SelectObject(draw.dc,FONT);
    let mut rect=draw.rect;DrawTextW(draw.dc,text.p(),-1,&mut rect,1|4|0x20);
}
unsafe extern "system" fn window_proc(hwnd:H,message:u32,w:usize,l:isize)->isize{
    if TASKBAR_MESSAGE!=0&&message==TASKBAR_MESSAGE{tray(0);return 0;}
    match message{
        0x0010=>{ShowWindow(hwnd,0);0},
        0x0002=>{UnregisterHotKey(hwnd,1);UnregisterHotKey(hwnd,2);tray(2);PostQuitMessage(0);0},
        0x000f=>{let mut paint:Paint=mem::zeroed();let dc=BeginPaint(hwnd,&mut paint);FillRect(dc,&paint.rect,BRUSH);EndPaint(hwnd,&paint);0},
        0x0138|0x0135=>{SetTextColor(w as H,if l==item(92)&&!STATUS_OK{0x8090f0}else{INK});SetBkColor(w as H,BG);BRUSH},
        0x0133=>{SetTextColor(w as H,INK);SetBkColor(w as H,FIELD);EDIT_BRUSH},
        0x002b=>{draw_button(&*(l as*const DrawItem));1},
        0x0111=>{match(w&0xffff)as i32{90=>save(),12=>browse(11),22=>browse(21),13=>open_folder(false),23=>open_folder(true),100=>capture(false),101=>capture(true),_=>{}}0},
        0x0312=>{if w==1{capture(false);}else if w==2{capture(true);}0},
        x if x==WM_APP+1=>{show_settings(false);1},
        x if x==WM_APP+2=>{show_settings(true);1},
        x if x==WM_APP+3=>{capture(false);1},
        x if x==WM_APP+4=>{capture(true);1},
        x if x==WM_APP+9=>{quit();1},
        x if x==WM_APP+10=>{(1|KEYS)as isize},
        x if x==WM_APP+20=>{CFG.microphone=w&1!=0;CFG.audio=w&2!=0;CFG.clicks=w&4!=0;if !config::persist(&CFG){status("Audioeinstellungen konnten nicht gespeichert werden.",false);}check(25,CFG.microphone);check(26,CFG.audio);check(27,CFG.clicks);1},
        x if x==WM_APP+30=>{if l==0x205{menu();}else if l==0x202||l==0x203{show_settings(false);}0},
        _=>DefWindowProcW(hwnd,message,w,l)
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn mainCRTStartup()->!{
    let mut argc=0;let argv=CommandLineToArgvW(GetCommandLineW(),&mut argc);let mut action=1;let mut override_path=Text::empty();
    for i in 1..argc{
        let arg=Text::from_ptr(*argv.add(i as usize));
        if arg.is("--self-test"){LocalFree(argv as H);ExitProcess(if config::self_test(){0}else{3});}
        if arg.is("--preview")&&i+1<argc{PREVIEW=true;override_path=Text::from_ptr(*argv.add((i+1)as usize));}
        if arg.is("--background"){action=0;}else if arg.is("--screenshot"){action=3;}else if arg.is("--recording"){action=4;}
        else if arg.is("--quit"){action=9;}else if arg.is("--probe"){action=10;}else if arg.is("recording"){action=2;}
    }
    LocalFree(argv as H);
    let mutex=CreateMutexW(ptr::null(),0,Text::of("Local\\MosaiksCaptureRust").p());let already=GetLastError()==183;
    if mutex==0{ExitProcess(4);}
    if already{
        let mut existing=0;for _ in 0..200{existing=FindWindowW(Text::of(CLASS).p(),ptr::null());if existing!=0{break;}Sleep(10);}
        if existing!=0{
            let mut pid=0;GetWindowThreadProcessId(existing,&mut pid);AllowSetForegroundWindow(pid);
            let mut response=0;if action!=0{SendMessageTimeoutW(existing,WM_APP+action,0,0,2,2000,&mut response);}
            CloseHandle(mutex);ExitProcess(if action==10&&response!=7{5}else{0});
        }
        CloseHandle(mutex);ExitProcess(6);
    }
    if action==9||action==10{CloseHandle(mutex);ExitProcess(if action==10{5}else{0});}
    SetProcessDpiAwarenessContext(-4);CoInitializeEx(ptr::null(),2);
    CFG=config::initialize(&override_path);if PREVIEW{CFG.autostart=false;SetEnvironmentVariableW(Text::of("MOSAIK_CAPTURE_TEST_CONFIG").p(),config::INI.p());}INSTANCE=GetModuleHandleW(ptr::null());SCALE=GetDpiForSystem()as i32;
    BRUSH=CreateSolidBrush(BG);EDIT_BRUSH=CreateSolidBrush(FIELD);
    let class=WndClass{style:3,proc:Some(window_proc),cls_extra:0,wnd_extra:0,instance:INSTANCE,icon:LoadIconW(INSTANCE,1usize as*const u16),cursor:LoadCursorW(0,32512usize as*const u16),background:BRUSH,menu:ptr::null(),name:ptr::null()};
    let class_name=Text::of(CLASS);let class=WndClass{name:class_name.p(),..class};
    if RegisterClassW(&class)==0{ExitProcess(7);}
    let mut rect=Rect{left:0,top:0,right:px(730),bottom:px(688)};AdjustWindowRectEx(&mut rect,0x00ca0000,0,0x40000);
    let mut area=Rect::default();SystemParametersInfoW(0x30,0,&mut area as*mut _ as*mut _,0);
    let width=rect.right-rect.left;let height=rect.bottom-rect.top;
    APP=CreateWindowExW(0x40000,class_name.p(),Text::of(TITLE).p(),0x00ca0000,area.left+(area.right-area.left-width)/2,area.top+(area.bottom-area.top-height)/2,width,height,0,0,INSTANCE,ptr::null());
    if APP==0{ExitProcess(8);}
    let dark:i32=1;DwmSetWindowAttribute(APP,20,&dark as*const _ as*const _,4);
    init_ui();TASKBAR_MESSAGE=RegisterWindowMessageW(Text::of("TaskbarCreated").p());tray(0);
    let registered=register(&CFG);let saved=config::persist(&CFG)&&(PREVIEW||config::login(CFG.autostart));
    if !registered{status("Ein Tastenkürzel ist bereits belegt. Bitte ein anderes wählen.",false);}else if !saved{status("Die Einstellungen oder der Autostart konnten nicht gespeichert werden.",false);}
    if action==1||action==2||!registered{show_settings(action==2);}else if action==3||action==4{capture(action==4);}
    let mut msg:Msg=mem::zeroed();
    while GetMessageW(&mut msg,0,0,0)>0{
        if msg.message==0x100&&msg.w==27{ShowWindow(APP,0);continue;}
        if IsDialogMessageW(APP,&mut msg)==0{TranslateMessage(&msg);DispatchMessageW(&msg);}
    }
    CoUninitialize();CloseHandle(mutex);ExitProcess(0)
}
