"""Exercise shipped Doorstop, CoreCLR and the managed entry point on a real Mac, without game code."""
import os,subprocess,sys,tempfile,zipfile
from pathlib import Path
root=Path(__file__).resolve().parents[1]
archive=Path(sys.argv[1]).resolve()
with tempfile.TemporaryDirectory(prefix='jibbers-injection-smoke-') as tmp:
    directory=Path(tmp)
    with zipfile.ZipFile(archive) as z:z.extractall(directory)
    mac=directory/'JibbersMods/mac'
    for library in mac.glob('*.dylib'):library.chmod(0o755)
    fake=directory/'FakeGameAssembly.dylib'; player=directory/'FakeUnityPlayer'
    subprocess.run(['clang','-dynamiclib',str(root/'mac/native/fakegame.c'),'-o',str(fake)],check=True)
    subprocess.run(['clang',str(root/'mac/native/doorstop_smoke.c'),'-o',str(player)],check=True)
    env=os.environ.copy()
    env.update(DOORSTOP_ENABLED='1',DOORSTOP_IGNORE_DISABLED_ENV='1',
        DOORSTOP_TARGET_ASSEMBLY=str(mac/'managed/Jibbers.Sdk.dll'),
        DOORSTOP_CLR_RUNTIME_CORECLR_PATH=str(mac/'runtime/osx-arm64/libcoreclr.dylib'),
        DOORSTOP_CLR_CORLIB_DIR=str(mac/'runtime/osx-arm64'),
        DYLD_INSERT_LIBRARIES=str(mac/'libdoorstop.dylib'),
        DYLD_LIBRARY_PATH=str(mac)+':'+str(mac/'runtime/osx-arm64'),
        JIBBERS_GAME_ROOT=str(directory),JIBBERS_GAME_APP=str(directory/'NoGame.app'))
    result=subprocess.run([str(player),str(fake)],cwd=directory,env=env,capture_output=True,text=True,timeout=30)
    log=directory/'jibbers_hotload.log'
    text=log.read_text() if log.exists() else ''
    print(result.stdout,result.stderr,text)
    assert result.returncode==0,result.returncode
    assert 'Native macOS loader' in text,'Doorstop did not execute the packaged managed host'
    assert 'No IL2CPP metadata in this app' in text,'The compatibility guard did not stop the fake game'
    assert 'READY:' not in text,'The fake game was incorrectly permitted to load mods'
    print('PASS: shipped Doorstop injected the bundled CoreCLR, executed the managed Mac host, and stopped an incompatible fake game before hooks.')
