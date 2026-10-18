//! Forwards every `version.dll` export to the real one in System32, so the game keeps working
//! while our DLL sits in its folder. Each export is a naked `jmp` through a pointer filled in
//! at attach time, so we never need to know the functions' signatures.

use std::arch::naked_asm;
use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
use windows_sys::Win32::System::SystemInformation::GetSystemDirectoryW;

macro_rules! forward {
    ($($name:ident),* $(,)?) => {
        #[allow(non_upper_case_globals)]
        mod real { $( pub static mut $name: usize = 0; )* }

        $(
            #[unsafe(no_mangle)]
            #[unsafe(naked)]
            pub unsafe extern "system" fn $name() {
                naked_asm!("jmp qword ptr [rip + {0}]", sym real::$name)
            }
        )*

        /// Loads System32\version.dll and resolves every forwarded export.
        pub unsafe fn init() {
            let mut buf = [0u16; 260];
            let n = GetSystemDirectoryW(buf.as_mut_ptr(), buf.len() as u32) as usize;
            let mut path: Vec<u16> = buf[..n].to_vec();
            path.extend("\\version.dll\0".encode_utf16());
            let module = LoadLibraryW(path.as_ptr());
            if module.is_null() {
                return;
            }
            $(
                let sym = concat!(stringify!($name), "\0");
                if let Some(f) = GetProcAddress(module, sym.as_ptr()) {
                    real::$name = f as usize;
                }
            )*
        }
    };
}

forward!(
    GetFileVersionInfoA,
    GetFileVersionInfoByHandle,
    GetFileVersionInfoExA,
    GetFileVersionInfoExW,
    GetFileVersionInfoSizeA,
    GetFileVersionInfoSizeExA,
    GetFileVersionInfoSizeExW,
    GetFileVersionInfoSizeW,
    GetFileVersionInfoW,
    VerFindFileA,
    VerFindFileW,
    VerInstallFileA,
    VerInstallFileW,
    VerLanguageNameA,
    VerLanguageNameW,
    VerQueryValueA,
    VerQueryValueW,
);
