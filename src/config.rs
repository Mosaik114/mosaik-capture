use crate::win::*;
use core::{ptr, mem};

#[derive(Clone, Copy)]
pub struct Text { pub buf: [u16;512] }
impl Text {
    pub const fn empty()->Self { Self{buf:[0;512]} }
    pub fn of(s:&str)->Self { let mut value=Self::empty(); value.append(s); value }
    pub fn len(&self)->usize { self.buf.iter().position(|c|*c==0).unwrap_or(511) }
    pub fn p(&self)->*const u16 { self.buf.as_ptr() }
    pub fn append(&mut self,s:&str) { let mut n=self.len(); for c in s.encode_utf16() { if n>=511 { break; } self.buf[n]=c;n+=1; }self.buf[n]=0; }
    pub fn extend(&mut self,s:&Text) { let mut n=self.len();for c in &s.buf[..s.len()] {if n>=511{break;}self.buf[n]=*c;n+=1;}self.buf[n]=0; }
    pub unsafe fn from_ptr(p:*const u16)->Self {let mut value=Self::empty();if !p.is_null(){for i in 0..511 {let c=*p.add(i);value.buf[i]=c;if c==0{break;}}}value}
    pub fn is(&self,s:&str)->bool {let value=Text::of(s);self.buf[..self.len()]==value.buf[..value.len()]}
}

#[derive(Clone, Copy, PartialEq, Eq)] pub struct Key {pub modifiers:u32,pub code:u32}
fn token_is(token:&[u16],word:&str)->bool {
    token.len()==word.len() && token.iter().zip(word.bytes()).all(|(a,b)| {
        let c=if *a>=97&&*a<=122{*a-32}else{*a}; c==b as u16
    })
}
pub fn parse_key(value:&Text)->Option<Key> {
    let mut token=[0u16;24];let mut count=0;let mut modifiers=0;let mut code=0;
    for c in value.buf[..value.len()].iter().copied().chain(core::iter::once(43)) {
        if c==32 {continue;}
        if c!=43 {if count>=24{return None;}token[count]=c;count+=1;continue;}
        if count==0{return None;}
        let t=&token[..count];
        let m=if token_is(t,"SHIFT")||token_is(t,"UMSCHALT"){4}else if token_is(t,"ALT"){1}else if token_is(t,"CTRL")||token_is(t,"CONTROL")||token_is(t,"STRG"){2}else{0};
        if m!=0 {if modifiers&m!=0{return None;}modifiers|=m;} else {
            if code!=0{return None;}
            code=if count==1 {
                let v=t[0];let upper=if v>=97&&v<=122{v-32}else{v};
                if (48..=57).contains(&upper)||(65..=90).contains(&upper){upper as u32}else{return None;}
            }else if token_is(t,"SPACE")||token_is(t,"LEERTASTE"){32}
            else if t[0]==70||t[0]==102 {
                let mut n=0;for digit in &t[1..] {if *digit<48||*digit>57{return None;}n=n*10+(*digit-48)as u32;}
                if n==0||n>24{return None;}111+n
            }else{return None;};
        }
        count=0;
    }
    if code==0||modifiers==0{None}else{Some(Key{modifiers,code})}
}

#[derive(Clone, Copy)]
pub struct Config {
    pub screenshot_key:Text,pub recording_key:Text,pub screenshot_folder:Text,pub recording_folder:Text,
    pub microphone:bool,pub audio:bool,pub clicks:bool,pub autostart:bool,pub fps:u32,pub countdown:u32,
}
impl Config {
    pub const fn empty()->Self {Self{screenshot_key:Text::empty(),recording_key:Text::empty(),screenshot_folder:Text::empty(),recording_folder:Text::empty(),microphone:false,audio:true,clicks:true,autostart:true,fps:60,countdown:3}}
}
pub static mut INI:Text=Text::empty();
pub static mut EXE:Text=Text::empty();
pub static mut ROOT:Text=Text::empty();

unsafe fn read_text(key:&str,fallback:&Text)->Text {
    let mut out=Text::empty();GetPrivateProfileStringW(Text::of("capture").p(),Text::of(key).p(),fallback.p(),out.buf.as_mut_ptr(),512,INI.p());out
}
unsafe fn number(key:&str,fallback:i32)->u32 {GetPrivateProfileIntW(Text::of("capture").p(),Text::of(key).p(),fallback,INI.p())}
unsafe fn folder(id:i32,suffix:&str)->Text {let mut value=Text::empty();SHGetFolderPathW(0,id,0,0,value.buf.as_mut_ptr());value.append(suffix);value}
pub unsafe fn initialize(override_path:&Text)->Config {
    GetModuleFileNameW(0,EXE.buf.as_mut_ptr(),512);ROOT=EXE;
    if let Some(n)=ROOT.buf[..ROOT.len()].iter().rposition(|c|*c==92){ROOT.buf[n]=0;}
    if override_path.len()!=0{INI=*override_path;}else{
        GetEnvironmentVariableW(Text::of("LOCALAPPDATA").p(),INI.buf.as_mut_ptr(),512);INI.append("\\Mosaiks-Aufnahme");CreateDirectoryW(INI.p(),ptr::null());INI.append("\\settings.ini");
    }
    Config{
        screenshot_key:read_text("screenshot_hotkey",&Text::of("Shift+Alt+4")),
        recording_key:read_text("recording_hotkey",&Text::of("Shift+Alt+5")),
        screenshot_folder:read_text("screenshot_folder",&folder(0x27,"\\Mosaiks-Screenshots")),
        recording_folder:read_text("recording_folder",&folder(0x0e,"\\Mosaiks-Aufnahmen")),
        microphone:number("microphone",0)!=0,audio:number("system_audio",1)!=0,clicks:number("show_clicks",1)!=0,
        autostart:number("autostart",1)!=0,fps:if number("fps",60)==30{30}else{60},
        countdown:match number("countdown",3){0=>0,5=>5,_=>3},
    }
}
unsafe fn write(path:&Text,key:&str,value:&Text)->bool {WritePrivateProfileStringW(Text::of("capture").p(),Text::of(key).p(),value.p(),path.p())!=0}
fn yes(value:bool)->Text {Text::of(if value{"1"}else{"0"})}
pub unsafe fn persist(c:&Config)->bool {
    let mut temp=INI;temp.append(".new");
    let file=CreateFileW(temp.p(),0x40000000,0,ptr::null(),2,0x80,0);
    if file == -1{return false;}
    let mut written=0;let bom=[0xffu8,0xfe];let bom_ok=WriteFile(file,bom.as_ptr(),2,&mut written,ptr::null_mut())!=0&&written==2;
    CloseHandle(file);if !bom_ok{return false;}
    let ok=write(&temp,"screenshot_hotkey",&c.screenshot_key)&&write(&temp,"recording_hotkey",&c.recording_key)
        &&write(&temp,"screenshot_folder",&c.screenshot_folder)&&write(&temp,"recording_folder",&c.recording_folder)
        &&write(&temp,"microphone",&yes(c.microphone))&&write(&temp,"system_audio",&yes(c.audio))&&write(&temp,"show_clicks",&yes(c.clicks))
        &&write(&temp,"autostart",&yes(c.autostart))&&write(&temp,"fps",&Text::of(if c.fps==30{"30"}else{"60"}))
        &&write(&temp,"countdown",&Text::of(match c.countdown{0=>"0",5=>"5",_=>"3"}));
    WritePrivateProfileStringW(ptr::null(),ptr::null(),ptr::null(),temp.p());
    ok&&MoveFileExW(temp.p(),INI.p(),1|8)!=0
}
pub unsafe fn login(enabled:bool)->bool {
    let mut key=0;let mut disposition=0;
    if RegCreateKeyExW(0x80000001u32 as i32 as isize,Text::of("Software\\Microsoft\\Windows\\CurrentVersion\\Run").p(),0,ptr::null(),0,2,ptr::null(),&mut key,&mut disposition)!=0{return false;}
    let result=if enabled {
        let mut command=Text::of("\"");command.extend(&EXE);command.append("\" --background");
        RegSetValueExW(key,Text::of("Mosaiks Aufnahme").p(),0,1,command.p()as*const u8,((command.len()+1)*2)as u32)==0
    }else{let status=RegDeleteValueW(key,Text::of("Mosaiks Aufnahme").p());status==0||status==2};
    RegCloseKey(key);result
}
pub fn valid_folder(value:&Text)->bool {
    let n=value.len();let p=&value.buf;
    n>=3 && ((p[1]==58&&(p[2]==92||p[2]==47))||(p[0]==92&&p[1]==92)) && !p[..n].iter().any(|c|[10,13,34,42,60,62,63,124].contains(c))
}
pub unsafe fn spawn_engine(recording:bool)->bool {
    let mut executable=ROOT;executable.append("\\Mosaiks-Capture-Engine.exe");
    let mut command=Text::of("\"");command.extend(&executable);command.append(if recording{"\" --recording"}else{"\" --screenshot"});
    let mut start:StartupInfo=mem::zeroed();start.size=mem::size_of::<StartupInfo>()as u32;
    let mut process:ProcessInfo=mem::zeroed();
    let ok=CreateProcessW(ptr::null(),command.buf.as_mut_ptr(),ptr::null(),ptr::null(),0,0x08000000,ptr::null(),ROOT.p(),&mut start,&mut process)!=0;
    if ok{CloseHandle(process.process);CloseHandle(process.thread);}ok
}

pub fn self_test()->bool {
    let cases=[("Shift+Alt+4",Some(Key{modifiers:5,code:52})),("Alt + Umschalt + 5",Some(Key{modifiers:5,code:53})),
        ("Strg+Shift+F12",Some(Key{modifiers:6,code:123})),("ctrl+space",Some(Key{modifiers:2,code:32})),
        ("Shift+Alt+F24",Some(Key{modifiers:5,code:135})),("Alt+F25",None),("Shift+Shift+4",None),
        ("Alt+4+5",None),("4",None),("Shift+Alt",None),("Alt++4",None),("Alt+F0",None)];
    cases.iter().all(|(text,expected)|parse_key(&Text::of(text))==*expected)
        &&valid_folder(&Text::of("C:\\Bilder\\Grüße"))&&!valid_folder(&Text::of("relativ\\Bilder"))
        &&!valid_folder(&Text::of("C:\\Bilder\nneu"))
}
