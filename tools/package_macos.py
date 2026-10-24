"""Build a self-contained native Mac tester ZIP. Never consumes the Windows catalog."""
import argparse, hashlib, io, json, os, shutil, subprocess, tarfile, tempfile, urllib.request, zipfile
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
DOBBY = "5dfc8546954ce3b3198132ab13fddb89ee92cdd7"
DOORSTOP_SHA256 = "8282e8ec37abc81bbf736d12c315fdb78b0418654fb57be9e40858649953ccb7"
def get(url):
    request = urllib.request.Request(url, headers={"User-Agent":"Jibbers-Mac-Preview-Builder"})
    with urllib.request.urlopen(request, timeout=180) as response: return response.read()
def run(*args): subprocess.run(args, cwd=ROOT, check=True)
def copy(src, dst):
    dst=Path(dst);dst.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(src,dst)
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--native',type=Path,required=True);parser.add_argument('--version',default='0.1.0');args=parser.parse_args()
    native=args.native.resolve()
    run('dotnet','build','mac/managed','-c','Release','-nologo','-v','q','-p:JibbersNoInstall=true')
    run('dotnet','run','--project','mac/tests','-c','Release','-p:JibbersNoInstall=true')
    for mod in ('AlpineVision','FPV','Reshred'):
        run('dotnet','build',f'mods/cs/{mod}','-c','Release','-nologo','-v','q','-p:JibbersNoInstall=true')
    commit=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
    dist=ROOT/'dist';dist.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='jibbers-mac-') as temporary:
        stage=Path(temporary)/'package';mods=stage/'JibbersMods';mac=mods/'mac';mac.mkdir(parents=True)
        copy(ROOT/'mac/Run Jibbers.command',stage/'Run Jibbers.command')
        copy(ROOT/'mac/README.md',mods/'README-Mac.md')
        copy(ROOT/'LICENSE',mods/'licenses/LICENSE')
        copy(ROOT/'mac/expected-build.json',mac/'expected-build.json')
        managed=ROOT/'mac/managed/bin/Release/net8.0'
        for name in ('Jibbers.Sdk.dll','Jibbers.Sdk.runtimeconfig.json','Jibbers.Sdk.deps.json'):
            copy(managed/name,mac/'managed'/name)
        for mod in ('AlpineVision','FPV','Reshred'):
            copy(ROOT/f'mods/cs/{mod}/bin/Release/net8.0/{mod}.dll',mods/f'cs/{mod}/{mod}.dll')
        copy(ROOT/'mac/assets/jibbers_alpinevision.bundle',mods/'packages/jibbers_atmosphere/jibbers_alpinevision.bundle')
        copy(ROOT/'mac/assets/jibbers_fpv.bundle',mods/'jibbers_fpv.bundle')
        for library in ('libjibbers_hooks.dylib','libjibbers_macos_ui.dylib'):
            run('lipo','-create',str(native/'arm64'/library),str(native/'x86_64'/library),'-output',str(mac/library))
        doorstop=get('https://github.com/NeighTools/UnityDoorstop/releases/download/v4.6.0/doorstop_macos_release_4.6.0.zip')
        if hashlib.sha256(doorstop).hexdigest()!=DOORSTOP_SHA256:raise ValueError('Doorstop checksum mismatch')
        with zipfile.ZipFile(io.BytesIO(doorstop)) as z:
            matches=[n for n in z.namelist() if n.endswith('/libdoorstop.dylib') or n=='libdoorstop.dylib']
            if len(matches)!=1:raise ValueError('Unexpected Doorstop layout: '+str(z.namelist()))
            (mac/'libdoorstop.dylib').write_bytes(z.read(matches[0]))
            license_name=next(n for n in z.namelist() if n.endswith('LICENSE'))
            (mods/'licenses/UnityDoorstop-LICENSE').write_bytes(z.read(license_name))
        (mods/'licenses/Dobby-LICENSE').write_bytes(get(f'https://raw.githubusercontent.com/jmpews/Dobby/{DOBBY}/LICENSE'))
        metadata=json.loads(get('https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json'))
        version=metadata['latest-runtime']
        release=next(r for r in metadata['releases'] if r.get('runtime',{}).get('version')==version)
        runtime_hashes={}
        for rid in ('osx-arm64','osx-x64'):
            file=next(f for f in release['runtime']['files'] if f.get('rid')==rid and f['name'].endswith('.tar.gz'))
            content=get(file['url'])
            if hashlib.sha512(content).hexdigest().lower()!=file['hash'].lower():raise ValueError('Microsoft runtime checksum mismatch for '+rid)
            runtime_hashes[rid]={'version':version,'url':file['url'],'sha512':file['hash']}
            rt=Path(temporary)/rid;rt.mkdir()
            with tarfile.open(fileobj=io.BytesIO(content),mode='r:gz') as tar:tar.extractall(rt,filter='data')
            shutil.copytree(rt/'shared/Microsoft.NETCore.App'/version,mac/'runtime'/rid)
            for notice in ('LICENSE.txt','ThirdPartyNotices.txt'):
                if (rt/notice).exists():copy(rt/notice,mods/'licenses/dotnet'/notice)
        for library in mac.glob('*.dylib'):
            run('lipo','-verify_arch','arm64','x86_64',str(library))
            run('codesign','--force','--sign','-',str(library))
            run('codesign','--verify',str(library))
        info={'version':args.version,'experimental':True,'source_commit':commit,'gameplay_verified':False,'architectures':['arm64','x86_64'],'dobby_commit':DOBBY,'doorstop':'4.6.0','doorstop_sha256':DOORSTOP_SHA256,'dotnet':runtime_hashes,'windows_catalog_enabled':False}
        (mac/'BUILD.json').write_text(json.dumps(info,indent=2))
        manifest={p.relative_to(stage).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in stage.rglob('*') if p.is_file()}
        (mac/'SHA256SUMS.json').write_text(json.dumps(manifest,indent=2,sort_keys=True))
        output=dist/f'JibbersModLoader-macOS-preview-{args.version}.zip'
        with zipfile.ZipFile(output,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=9) as z:
            for p in sorted(stage.rglob('*')):
                if not p.is_file():continue
                relative=p.relative_to(stage).as_posix()
                entry=zipfile.ZipInfo(relative);entry.create_system=3
                executable=relative.endswith(('.command','.dylib'))
                entry.external_attr=((0o100755 if executable else 0o100644)<<16)
                entry.compress_type=zipfile.ZIP_DEFLATED;z.writestr(entry,p.read_bytes())
        with zipfile.ZipFile(output) as z:
            assert z.testzip() is None
            for name,digest in manifest.items():assert hashlib.sha256(z.read(name)).hexdigest()==digest,name
            assert {n.split('/')[0] for n in z.namelist()}=={'Run Jibbers.command','JibbersMods'}
            assert not any(n.lower().endswith(('version.dll','jibbers_loader.dll','gameassembly.dll','jibbers.exe')) for n in z.namelist())
        # Complete source for the preview, including the exact Doorstop and Dobby revisions.
        source=dist/f'JibbersModLoader-macOS-preview-{args.version}-source.zip'
        repository_source=subprocess.check_output(['git','archive','--format=zip','HEAD'],cwd=ROOT)
        with zipfile.ZipFile(source,'w',compression=zipfile.ZIP_DEFLATED) as z:
            z.writestr('JibbersModLoader-source.zip',repository_source)
            z.writestr('UnityDoorstop-v4.6.0-source.zip',get('https://codeload.github.com/NeighTools/UnityDoorstop/zip/refs/tags/v4.6.0'))
            z.writestr(f'Dobby-{DOBBY}-source.zip',get(f'https://codeload.github.com/jmpews/Dobby/zip/{DOBBY}'))
            z.writestr('BUILD.json',json.dumps(info,indent=2))
        (dist/'Mac-preview-verification.json').write_text(json.dumps({'package':output.name,'sha256':hashlib.sha256(output.read_bytes()).hexdigest(),'file_count':len(manifest)+1,'verified':True,**info},indent=2))
        print('Verified native Mac preview:',output)
if __name__=='__main__':main()
