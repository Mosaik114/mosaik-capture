"""Terminal-Pruefung ausschliesslich der eigenen App-Fenster."""
import ctypes as c
from ctypes import wintypes as w
from pathlib import Path
import subprocess
import sys
import time
import json

ROOT=Path(__file__).resolve().parent
QA=ROOT/'qa'
QA.mkdir(exist_ok=True)
u=c.WinDLL('user32',use_last_error=True)
g=c.WinDLL('gdi32',use_last_error=True)
u.FindWindowW.argtypes=[w.LPCWSTR,w.LPCWSTR];u.FindWindowW.restype=w.HWND
u.FindWindowExW.argtypes=[w.HWND,w.HWND,w.LPCWSTR,w.LPCWSTR];u.FindWindowExW.restype=w.HWND
u.IsWindowVisible.argtypes=[w.HWND]
u.RegisterHotKey.argtypes=[w.HWND,c.c_int,w.UINT,w.UINT]
u.UnregisterHotKey.argtypes=[w.HWND,c.c_int]
u.GetDlgItem.argtypes=[w.HWND,c.c_int];u.GetDlgItem.restype=w.HWND
u.GetWindowTextW.argtypes=[w.HWND,w.LPWSTR,c.c_int]
u.SendMessageW.argtypes=[w.HWND,w.UINT,w.WPARAM,w.LPARAM];u.SendMessageW.restype=w.LPARAM
u.GetWindowRect.argtypes=[w.HWND,c.POINTER(w.RECT)]
u.PrintWindow.argtypes=[w.HWND,w.HDC,w.UINT]
u.GetWindowDC.argtypes=[w.HWND];u.GetWindowDC.restype=w.HDC
u.ReleaseDC.argtypes=[w.HWND,w.HDC]
g.CreateCompatibleDC.argtypes=[w.HDC];g.CreateCompatibleDC.restype=w.HDC
g.SelectObject.argtypes=[w.HDC,w.HGDIOBJ];g.SelectObject.restype=w.HGDIOBJ
g.DeleteObject.argtypes=[w.HGDIOBJ];g.DeleteDC.argtypes=[w.HDC]

def window(title=None):
    for _ in range(80):
        hwnd=u.FindWindowW('MosaiksAufnahmeRust' if title is None else None,title)
        if title is not None:
            while hwnd and not u.IsWindowVisible(hwnd):hwnd=u.FindWindowExW(None,hwnd,None,title)
        if hwnd:return hwnd
        time.sleep(.1)
    raise RuntimeError('Eigenes App-Fenster fehlt: '+str(title))

def text(hwnd):
    value=c.create_unicode_buffer(1024)
    u.SendMessageW(hwnd,0x000d,len(value),c.cast(value,c.c_void_p).value)
    return value.value

def snapshot(hwnd,name):
    from PIL import Image
    class Header(c.Structure):
        _fields_=[('size',w.DWORD),('width',w.LONG),('height',w.LONG),('planes',w.WORD),('bits',w.WORD),('compression',w.DWORD),('image_size',w.DWORD),('x',w.LONG),('y',w.LONG),('used',w.DWORD),('important',w.DWORD)]
    rect=w.RECT();u.GetWindowRect(hwnd,c.byref(rect));width=rect.right-rect.left;height=rect.bottom-rect.top
    header=Header(c.sizeof(Header),width,-height,1,32,0,width*height*4,0,0,0,0)
    g.CreateDIBSection.argtypes=[w.HDC,c.POINTER(Header),w.UINT,c.POINTER(c.c_void_p),w.HANDLE,w.DWORD];g.CreateDIBSection.restype=w.HBITMAP
    dc=u.GetWindowDC(hwnd);memory=g.CreateCompatibleDC(dc);bits=c.c_void_p()
    bitmap=g.CreateDIBSection(dc,c.byref(header),0,c.byref(bits),None,0)
    old=g.SelectObject(memory,bitmap)
    try:
        if not u.PrintWindow(hwnd,memory,2):raise c.WinError(c.get_last_error())
        image=Image.frombytes('RGB',(width,height),c.string_at(bits,width*height*4),'raw','BGRX')
        image.save(QA/name)
    finally:g.SelectObject(memory,old);g.DeleteObject(bitmap);g.DeleteDC(memory);u.ReleaseDC(hwnd,dc)
    print(str(QA/name))

mode=sys.argv[1]
if mode=='start':
    (QA/'settings.ini').write_text('[capture]\nscreenshot_folder='+str(QA/'screenshots')+'\nrecording_folder='+str(QA/'videos')+'\nfps=30\ncountdown=0\nmicrophone=0\nsystem_audio=0\nshow_clicks=0\nautostart=0\n',encoding='utf-16')
    subprocess.Popen([str(ROOT/'bin/Mosaiks-Aufnahme.exe'),'--preview',str(QA/'settings.ini')])
    hwnd=window()
    for _ in range(80):
        values={str(i):text(u.GetDlgItem(hwnd,i))for i in [10,11,20,21,92]}
        if values['10']=='Shift+Alt+4' and values['20']=='Shift+Alt+5':break
        time.sleep(.1)
    assert values['10']=='Shift+Alt+4' and values['20']=='Shift+Alt+5',values
    u.SendMessageW(hwnd,0x111,90,0)
    assert 'Gespeichert' in text(u.GetDlgItem(hwnd,92)),text(u.GetDlgItem(hwnd,92))
    probe=subprocess.run([str(ROOT/'bin/Mosaiks-Aufnahme.exe'),'--probe'],timeout=5)
    assert probe.returncode==0,probe.returncode
    snapshot(hwnd,'settings.png');print(json.dumps({'settings_verified':True,'fields':values}))
elif mode=='snapshot':snapshot(window(),'settings.png')
elif mode in ('selection','recording-selection'):
    subprocess.run([str(ROOT/'bin/Mosaiks-Aufnahme.exe'),'--recording' if mode=='recording-selection' else '--screenshot'],check=True,timeout=5)
    overlay=window('Mosaiks-Screenshot');time.sleep(.25)
    # Escape wird nur an den Auswahl-Dialog der eigenen App geschickt.
    u.SendMessageW(overlay,0x100,27,0);u.SendMessageW(overlay,0x101,27,0)
    for _ in range(20):
        if not u.IsWindowVisible(overlay):break
        time.sleep(.05)
    assert not u.IsWindowVisible(overlay),'Auswahlfenster wurde nicht abgebrochen'
    worker=u.FindWindowW(None,'Mosaiks Capture Engine')
    assert worker and u.SendMessageW(worker,0x800a,0,0)==0,'Aufnahme-Auswahl bleibt aktiv'
    print(mode+': Auswahl ueber Rust-Host geoeffnet und abgebrochen.')
elif mode=='hotkey-conflict':
    hwnd=window();old=text(u.GetDlgItem(hwnd,10))
    assert u.RegisterHotKey(None,9876,5,135),'Testkuerzel F24 ist schon belegt'
    try:
        replacement=c.create_unicode_buffer('Shift+Alt+F24')
        u.SendMessageW(u.GetDlgItem(hwnd,10),0x000c,0,c.cast(replacement,c.c_void_p).value)
        u.SendMessageW(hwnd,0x111,90,0)
        assert 'belegt' in text(u.GetDlgItem(hwnd,92)),text(u.GetDlgItem(hwnd,92))
        assert subprocess.run([str(ROOT/'bin/Mosaiks-Aufnahme.exe'),'--probe'],timeout=5).returncode==0
    finally:
        u.UnregisterHotKey(None,9876)
        replacement=c.create_unicode_buffer(old)
        u.SendMessageW(u.GetDlgItem(hwnd,10),0x000c,0,c.cast(replacement,c.c_void_p).value)
        u.SendMessageW(hwnd,0x111,90,0)
    assert 'Gespeichert' in text(u.GetDlgItem(hwnd,92))
    print('Kuerzel-Konflikt abgefangen; bisherige Kuerzel bleiben registriert.')
elif mode=='stop':
    subprocess.run([str(ROOT/'bin/Mosaiks-Aufnahme.exe'),'--quit'],check=True,timeout=5)
    print('Vorschau beendet.')
