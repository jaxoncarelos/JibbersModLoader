"""Offline release integrity/PE checks. Never loads a DLL or opens the game.

Also checks the game-build guard: the compatibility list (sdk/compat/compat.json) is valid, the
loader carries exactly that list, and every archive's README names each validated game build.
`--builds-block` prints the validated-builds text; package_sdk.ps1 runs it to check sdk/compat/compat.json.
"""
import hashlib
import json
import struct
import sys
import zipfile
from pathlib import Path, PurePosixPath

COMPAT = Path(__file__).resolve().parent.parent / 'sdk' / 'compat' / 'compat.json'
PLACEHOLDER = '{{VALIDATED_BUILDS}}'


def load_compat(path=COMPAT):
    """Returns (raw bytes, builds). Raises AssertionError if the list is unusable."""
    raw = Path(path).read_bytes()
    data = json.loads(raw.decode('utf-8-sig'))
    assert data.get('schema') == 1, 'compat.json: unsupported schema'
    builds = data.get('builds')
    assert isinstance(builds, list) and builds, 'compat.json: no validated builds'
    for b in builds:
        label = b.get('label')
        assert label, 'compat.json: a build has no label'
        assert isinstance(b.get('assemblySize'), int) and b['assemblySize'] > 0, f'{label}: bad assemblySize'
        assert len(b.get('headerHash', '')) == 16, f'{label}: bad headerHash'
        assert b.get('unity'), f'{label}: no unity version'
    return raw, builds


def builds_block(builds):
    lines = ['These releases were checked against, and only enable themselves on, these game builds:']
    for b in builds:
        when = f", validated {b['validated']}" if b.get('validated') else ''
        lines.append(f"  - {b['label']}")
        lines.append(f"    GameAssembly.dll {b['assemblySize']:,} bytes, Unity {b['unity']}{when}")
    lines.append('On any other build the loader switches the mods off (nothing is hooked), logs one line to')
    lines.append('jibbers_hotload.log and writes JibbersMods\\MODS-DISABLED.txt. Find your build in Steam: right-click')
    lines.append('the game > Properties > Installed Files > Browse local files, then GameAssembly.dll > Properties.')
    return '\n'.join(lines)


def pe_exports(data):
    def u16(offset):
        return struct.unpack_from('<H', data, offset)[0]

    def u32(offset):
        return struct.unpack_from('<I', data, offset)[0]

    assert data[:2] == b'MZ', 'Not a PE binary'
    pe = u32(0x3c)
    assert data[pe:pe + 4] == b'PE\0\0'
    assert u16(pe + 4) == 0x8664, 'Expected x64'
    optional = pe + 24
    assert u16(optional) == 0x20b, 'Expected PE32+'
    sections = optional + u16(pe + 20)

    def offset(rva):
        for i in range(u16(pe + 6)):
            section = sections + 40 * i
            start, size, raw = u32(section + 12), u32(section + 16), u32(section + 20)
            if start <= rva < start + size:
                return raw + rva - start
        raise AssertionError(f'Unmapped RVA {rva:x}')

    def string(rva):
        at = offset(rva)
        return data[at:data.index(b'\0', at)].decode('ascii')

    directory = offset(u32(optional + 112))
    names = offset(u32(directory + 32))
    exports = {string(u32(names + 4 * i)) for i in range(u32(directory + 24))}
    imports = set()
    import_rva = u32(optional + 120)
    if import_rva:
        entry = offset(import_rva)
        while any(data[entry:entry + 20]):
            imports.add(string(u32(entry + 12)).lower())
            entry += 20
    return exports, imports


def verify(folder):
    compat_raw, compat_builds = load_compat()
    manifest = json.loads((folder / 'mod-release-manifest.json').read_text(encoding='utf-8-sig'))
    payloads = {entry['path']: entry for entry in manifest['payloads']}
    plugin_exports = {'jh_core_init', 'jh_core_handle', 'jh_core_free', 'jh_core_unload'}
    for archive in manifest['archives']:
        path = folder / archive['path']
        assert hashlib.sha256(path.read_bytes()).hexdigest() == archive['sha256'], path
        expected = {entry['path']: entry for entry in archive['files']}
        with zipfile.ZipFile(path) as z:
            actual = {}
            for item in z.infolist():
                if item.is_dir():
                    continue
                name = item.filename.replace('\\', '/')
                assert not PurePosixPath(name).is_absolute() and '..' not in PurePosixPath(name).parts
                assert name not in actual, f'Duplicate ZIP entry: {name}'
                actual[name] = z.read(item)
            assert actual.keys() == expected.keys(), f'Wrong contents: {path}'
            assert 'README.txt' in actual
            readme = actual['README.txt'].decode('utf-8-sig')
            assert PLACEHOLDER not in readme, 'README still has the {{VALIDATED_BUILDS}} placeholder'
            for b in compat_builds:
                assert b['label'] in readme and f"{b['assemblySize']:,}" in readme, f"README does not state validated build {b['label']!r}"
            assert 'JIBBERS_HOTLOAD_FORCE' in readme, 'README must document the force override'
            for name, data in actual.items():
                entry = expected[name]
                assert len(data) == entry['bytes'], name
                digest = hashlib.sha256(data).hexdigest()
                assert digest == entry['sha256'], name
                if name in payloads:
                    assert digest == payloads[name]['sha256'], f'Inconsistent payload: {name}'
                assert name != 'jibbers_hotload_core.dll', 'Developer core must not ship'
                assert not name.endswith(('.pdb', '.json', '.log')), f'Private/debug file: {name}'
                if name.endswith('.dll'):
                    exports, imports = pe_exports(data)
                    if name == 'version.dll':
                        assert {'GetFileVersionInfoW', 'GetFileVersionInfoSizeW', 'VerQueryValueW'} <= exports
                        assert 'ws2_32.dll' not in imports, 'Release loader must not link TCP sockets'
                        assert compat_raw in data, 'version.dll was built with a different sdk/compat/compat.json'
                    else:
                        assert plugin_exports <= exports, f'Plugin ABI exports missing: {name}'
                if name == 'jibbers_atmosphere.bundle':
                    assert data.startswith(b'UnityFS\0'), 'Invalid Unity asset bundle'
            if any(name.endswith('jibbers_atmosphere.dll') for name in actual):
                assert 'jibbers_atmosphere.bundle' in actual
                assert 'SNOW-TEXTURES-LICENSE.txt' in actual
        print(f'OK {path.name}: {len(expected)} files, hashes and payloads verified')
    print('All release archives verified offline (not an in-game acceptance test).')


if __name__ == '__main__':
    if '--builds-block' in sys.argv:
        print(builds_block(load_compat()[1]))
    else:
        verify(Path(sys.argv[1] if len(sys.argv) > 1 else 'dist'))