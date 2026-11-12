"""Build the native launcher around a self-contained *directory* publish.
No downloaded runtime, system installation, shell extraction or administrator
permission is needed by the delivered EXE. Run on Windows after dotnet publish.
"""
from pathlib import Path
import argparse, ctypes, hashlib, json, os, struct, subprocess, zipfile

def sha(path):
    with path.open('rb') as stream: return hashlib.file_digest(stream,'sha256').hexdigest()

def version_block(key,value=b'',children=b'',text=False):
    head=struct.pack('<HHH',0,len(value)//2 if text else len(value),int(text))+key.encode('utf-16le')+b'\0\0'
    head+=b'\0'*(-len(head)%4)
    result=head+value
    if children: result+=b'\0'*(-len(result)%4)+children
    result+=b'\0'*(-len(result)%4)
    return struct.pack('<H',len(result))+result[2:]

def stamp_exe(path, icon):
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    begin=kernel.BeginUpdateResourceW; begin.argtypes=[ctypes.c_wchar_p,ctypes.c_int]; begin.restype=ctypes.c_void_p
    update=kernel.UpdateResourceW; update.argtypes=[ctypes.c_void_p,ctypes.c_void_p,ctypes.c_void_p,ctypes.c_ushort,ctypes.c_void_p,ctypes.c_uint]; update.restype=ctypes.c_int
    end=kernel.EndUpdateResourceW; end.argtypes=[ctypes.c_void_p,ctypes.c_int]; end.restype=ctypes.c_int
    handle=begin(str(path),False)
    if not handle: raise ctypes.WinError(ctypes.get_last_error())
    def put(kind,name,data):
        buffer=ctypes.create_string_buffer(data)
        if not update(handle,kind,name,0x409,buffer,len(data)): raise ctypes.WinError(ctypes.get_last_error())
    try:
        ico=icon.read_bytes(); reserved,kind,count=struct.unpack_from('<HHH',ico)
        if reserved or kind!=1: raise ValueError('invalid icon')
        group=struct.pack('<HHH',0,1,count)
        for i in range(count):
            width,height,palette,zero,planes,bpp,size,offset=struct.unpack_from('<BBBBHHII',ico,6+i*16)
            put(3,i+1,ico[offset:offset+size])
            group+=struct.pack('<BBBBHHIH',width,height,palette,zero,planes,bpp,size,i+1)
        put(14,1,group)
        fixed=struct.pack('<13I',0xFEEF04BD,0x10000,2<<16|9,0,2<<16|9,0,0x3f,0,0x40004,1,0,0,0)
        pairs={'FileDescription':'PRO2 手柄助手','ProductName':'PRO2 手柄助手','FileVersion':'2.9.0.0','ProductVersion':'2.9.0','OriginalFilename':'PRO2ControllerAssistant-v2.9.exe'}
        strings=b''.join(version_block(k,(v+'\0').encode('utf-16le'),text=True) for k,v in pairs.items())
        table=version_block('040904b0',children=strings)
        string_info=version_block('StringFileInfo',children=table)
        var=version_block('VarFileInfo',children=version_block('Translation',struct.pack('<HH',0x409,1200)))
        put(16,1,version_block('VS_VERSION_INFO',fixed,string_info+var))
        manifest=b'''<?xml version="1.0" encoding="UTF-8" standalone="yes"?><assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0"><trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false"/></requestedPrivileges></security></trustInfo><compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/></application></compatibility></assembly>'''
        put(24,1,manifest)
    except:
        end(handle,True); raise
    if not end(handle,False): raise ctypes.WinError(ctypes.get_last_error())

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('publish',type=Path); parser.add_argument('output',type=Path); parser.add_argument('--go',type=Path,required=True)
    args=parser.parse_args(); root=Path(__file__).resolve().parent
    publish=args.publish.resolve(); output=args.output.resolve()
    if not (publish/'PRO2ControllerAssistant.dll').is_file(): raise RuntimeError('requires directory publish, not a single-file publish')
    notices=publish/'Notices'; notices.mkdir(exist_ok=True)
    notice_sources={
        'PRO2-project-LICENSE.txt':root.parents[3]/'LICENSE',
        'Go-LICENSE.txt':args.go.parent.parent/'LICENSE',
        'VIIPER-LICENSE.txt':root.parent/'viiper'/'haptic-v0.8.0'/'LICENSE.txt',
        'USBIP-LICENSE.txt':root.parent/'usbip-win2'/'v0.9.7.7'/'LICENSE.txt',
    }
    nuget=Path(os.environ.get('NUGET_PACKAGES',str(Path.home()/'.nuget'/'packages')))
    runtime_config=json.loads((publish/'PRO2ControllerAssistant.runtimeconfig.json').read_text('utf-8'))
    for framework in runtime_config['runtimeOptions'].get('includedFrameworks',[]):
        if framework['name']=='Microsoft.NETCore.App':
            package=nuget/'microsoft.netcore.app.runtime.win-x64'/framework['version']
            notice_sources.update({'dotnet-LICENSE.txt':package/'LICENSE.TXT','dotnet-THIRD-PARTY-NOTICES.txt':package/'THIRD-PARTY-NOTICES.TXT'})
        if framework['name']=='Microsoft.WindowsDesktop.App':
            notice_sources['WPF-LICENSE.txt']=nuget/'microsoft.windowsdesktop.app.runtime.win-x64'/framework['version']/'LICENSE'
    for name,original in notice_sources.items(): (notices/name).write_bytes(original.read_bytes())
    entries=sorted(p for p in publish.rglob('*') if p.is_file() and p.suffix.lower() not in ['.pdb','.zip'])
    files=[{'name':p.relative_to(publish).as_posix(),'size':p.stat().st_size,'sha256':sha(p)} for p in entries]
    with zipfile.ZipFile(root/'payload.zip','w',zipfile.ZIP_DEFLATED,compresslevel=6) as package:
        for p in entries: package.write(p,p.relative_to(publish).as_posix())
    (root/'payload-manifest.json').write_text(json.dumps(files,ensure_ascii=False,separators=(',',':')),encoding='utf-8')
    env=os.environ.copy(); env.update(GOROOT=str(args.go.parent.parent),GOPROXY='off',CGO_ENABLED='0',GOCACHE=str(output.parent/'go-cache'))
    subprocess.run([str(args.go),'test','./...','-count=1'],cwd=root,env=env,check=True)
    subprocess.run([str(args.go),'build','-trimpath','-ldflags=-s -w -H=windowsgui','-o',str(output),'.'],cwd=root,env=env,check=True)
    stamp_exe(output,root.parent.parent/'windows'/'v60_viiper_app'/'Assets'/'AppIcon-v2.ico')
    print(json.dumps({'output':str(output),'bytes':output.stat().st_size,'sha256':sha(output),'runtime_bytes':sum(f['size'] for f in files),'files':len(files)},ensure_ascii=False))

if __name__=='__main__': main()
