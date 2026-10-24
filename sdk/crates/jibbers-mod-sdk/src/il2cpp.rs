//! Minimal bindings to the IL2CPP embedding API exported by GameAssembly.dll.
//!
//! Everything is resolved by name at runtime, so this works on any metadata version
//! (the reason MelonLoader/BepInEx fail on this game is their offline metadata parsing,
//! which we never do).

#![allow(non_camel_case_types, dead_code)]

use std::ffi::{c_char, c_void, CStr, CString};
use std::sync::OnceLock;
#[cfg(windows)]
use windows_sys::Win32::System::LibraryLoader::{GetModuleHandleA, GetProcAddress};

unsafe fn loaded_export(name: &str) -> Result<*mut c_void, String> {
    #[cfg(windows)]
    {
        let module = GetModuleHandleA(c"GameAssembly.dll".as_ptr() as _);
        if module.is_null() { return Err("GameAssembly.dll not loaded".into()); }
        GetProcAddress(module, name.as_ptr()).map(|f| f as *mut c_void).ok_or_else(|| format!("missing export {name:?}"))
    }
    #[cfg(target_os = "macos")]
    {
        unsafe extern "C" { fn dlsym(handle: *mut c_void, symbol: *const c_char) -> *mut c_void; fn dlopen(path: *const c_char, mode: i32) -> *mut c_void; }
        let mut symbol = dlsym(-2isize as _, name.as_ptr() as _);
        if symbol.is_null() {
            if let Ok(path) = std::env::var("JIBBERS_IL2CPP_LIBRARY") {
                let path = CString::new(path).map_err(|e| e.to_string())?;
                let handle = dlopen(path.as_ptr(), 2);
                if !handle.is_null() { symbol = dlsym(handle, name.as_ptr() as _); }
            }
        }
        if symbol.is_null() { Err(format!("missing loaded macOS export {name:?}")) } else { Ok(symbol) }
    }
}

pub type Domain = c_void;
pub type Assembly = c_void;
pub type Image = c_void;
pub type Class = c_void;
pub type Type = c_void;
pub type Field = c_void;
pub type Method = c_void;
pub type Property = c_void;
pub type Object = c_void;
pub type Thread = c_void;

macro_rules! api {
    ($($name:ident : fn($($arg:ty),*) $(-> $ret:ty)?;)*) => {
        pub struct Api { $( pub $name: unsafe extern "C" fn($($arg),*) $(-> $ret)?, )* }

        impl Api {
            unsafe fn load() -> Result<Api, String> {
                Ok(Api { $(
                    $name: {
                        let sym = concat!("il2cpp_", stringify!($name), "\0");
                        let f = loaded_export(sym)?;
                        std::mem::transmute(f)
                    },
                )* })
            }
        }
    };
}

api! {
    domain_get: fn() -> *mut Domain;
    domain_get_assemblies: fn(*const Domain, *mut usize) -> *mut *mut Assembly;
    assembly_get_image: fn(*const Assembly) -> *const Image;
    image_get_name: fn(*const Image) -> *const c_char;
    image_get_class_count: fn(*const Image) -> usize;
    image_get_class: fn(*const Image, usize) -> *const Class;
    thread_attach: fn(*mut Domain) -> *mut Thread;
    thread_detach: fn(*mut Thread);
    class_from_name: fn(*const Image, *const c_char, *const c_char) -> *mut Class;
    class_get_name: fn(*const Class) -> *const c_char;
    class_get_namespace: fn(*const Class) -> *const c_char;
    class_get_parent: fn(*const Class) -> *mut Class;
    class_get_fields: fn(*const Class, *mut *mut c_void) -> *mut Field;
    class_get_methods: fn(*const Class, *mut *mut c_void) -> *const Method;
    class_get_properties: fn(*const Class, *mut *mut c_void) -> *const Property;
    class_get_method_from_name: fn(*const Class, *const c_char, i32) -> *const Method;
    class_get_field_from_name: fn(*const Class, *const c_char) -> *mut Field;
    class_get_type: fn(*const Class) -> *const Type;
    class_is_valuetype: fn(*const Class) -> bool;
    class_is_assignable_from: fn(*const Class, *const Class) -> bool;
    class_get_nested_types: fn(*const Class, *mut *mut c_void) -> *mut Class;
    class_get_interfaces: fn(*mut Class, *mut *mut c_void) -> *mut Class;
    class_get_declaring_type: fn(*const Class) -> *mut Class;
    class_from_type: fn(*const Type) -> *mut Class;
    class_from_system_type: fn(*mut Object) -> *mut Class;
    class_get_element_class: fn(*mut Class) -> *mut Class;
    class_value_size: fn(*mut Class, *mut u32) -> i32;
    runtime_class_init: fn(*mut Class);
    field_get_name: fn(*const Field) -> *const c_char;
    field_get_type: fn(*const Field) -> *const Type;
    field_get_offset: fn(*const Field) -> usize;
    field_get_flags: fn(*const Field) -> i32;
    field_get_value: fn(*mut Object, *const Field, *mut c_void);
    field_set_value: fn(*mut Object, *const Field, *mut c_void);
    field_static_get_value: fn(*const Field, *mut c_void);
    field_static_set_value: fn(*const Field, *mut c_void);
    method_get_name: fn(*const Method) -> *const c_char;
    method_get_param_count: fn(*const Method) -> u32;
    method_get_param: fn(*const Method, u32) -> *const Type;
    method_get_param_name: fn(*const Method, u32) -> *const c_char;
    method_get_return_type: fn(*const Method) -> *const Type;
    method_get_flags: fn(*const Method, *mut u32) -> u32;
    property_get_name: fn(*const Property) -> *const c_char;
    property_get_get_method: fn(*const Property) -> *const Method;
    property_get_set_method: fn(*const Property) -> *const Method;
    type_get_name: fn(*const Type) -> *mut c_char;
    type_get_type: fn(*const Type) -> i32;
    type_get_object: fn(*const Type) -> *mut Object;
    free: fn(*mut c_void);
    runtime_invoke: fn(*const Method, *mut c_void, *mut *mut c_void, *mut *mut Object) -> *mut Object;
    object_get_class: fn(*mut Object) -> *mut Class;
    object_unbox: fn(*mut Object) -> *mut c_void;
    object_new: fn(*const Class) -> *mut Object;
    value_box: fn(*mut Class, *mut c_void) -> *mut Object;
    string_new_len: fn(*const c_char, u32) -> *mut Object;
    string_length: fn(*mut Object) -> i32;
    string_chars: fn(*mut Object) -> *const u16;
    array_new: fn(*mut Class, usize) -> *mut Object;
    array_length: fn(*mut Object) -> u32;
    array_new_full: fn(*mut Class, *mut usize, *mut usize) -> *mut Object;
    bounded_array_class_get: fn(*mut Class, u32, bool) -> *mut Class;
    array_class_get: fn(*mut Class, u32) -> *mut Class;
    format_exception: fn(*const Object, *mut c_char, i32);
    resolve_icall: fn(*const c_char) -> *mut c_void;
}

static API: OnceLock<Api> = OnceLock::new();

pub fn api() -> &'static Api {
    API.get().expect("il2cpp api not initialised")
}

pub unsafe fn init() -> Result<(), String> {
    let a = Api::load()?;
    let _ = API.set(a);
    Ok(())
}

pub unsafe fn cstr(p: *const c_char) -> String {
    if p.is_null() {
        String::new()
    } else {
        CStr::from_ptr(p).to_string_lossy().into_owned()
    }
}

pub unsafe fn type_name(t: *const Type) -> String {
    if t.is_null() {
        return "void".into();
    }
    let p = (api().type_get_name)(t);
    let s = cstr(p);
    (api().free)(p as _);
    s
}

pub unsafe fn assemblies() -> Vec<(*const Image, String)> {
    let a = api();
    let mut n = 0usize;
    let list = (a.domain_get_assemblies)((a.domain_get)(), &mut n);
    (0..n)
        .map(|i| {
            let img = (a.assembly_get_image)(*list.add(i));
            (img, cstr((a.image_get_name)(img)))
        })
        .collect()
}

pub unsafe fn image(name: &str) -> Option<*const Image> {
    assemblies().into_iter().find(|(_, n)| n == name).map(|(i, _)| i)
}

/// Finds a class by namespace + name in a named image (e.g. "UnityEngine.TerrainModule.dll").
pub unsafe fn class(image_name: &str, ns: &str, name: &str) -> Option<*mut Class> {
    let img = image(image_name)?;
    let ns = CString::new(ns).ok()?;
    let name = CString::new(name).ok()?;
    let k = (api().class_from_name)(img, ns.as_ptr(), name.as_ptr());
    (!k.is_null()).then_some(k)
}

/// Method lookup by name and parameter count (-1 = any).
pub unsafe fn method(k: *const Class, name: &str, argc: i32) -> Option<*const Method> {
    let c = CString::new(name).ok()?;
    let mut cur = k;
    while !cur.is_null() {
        let m = (api().class_get_method_from_name)(cur, c.as_ptr(), argc);
        if !m.is_null() {
            return Some(m);
        }
        cur = (api().class_get_parent)(cur);
    }
    None
}

/// All methods named `name` with `argc` params whose parameter type names match `sig`.
pub unsafe fn method_sig(k: *const Class, name: &str, sig: &[&str]) -> Option<*const Method> {
    let a = api();
    let mut it: *mut c_void = std::ptr::null_mut();
    loop {
        let m = (a.class_get_methods)(k, &mut it);
        if m.is_null() {
            return None;
        }
        if cstr((a.method_get_name)(m)) != name || (a.method_get_param_count)(m) as usize != sig.len() {
            continue;
        }
        if sig.iter().enumerate().all(|(i, s)| type_name((a.method_get_param)(m, i as u32)) == *s) {
            return Some(m);
        }
    }
}

pub unsafe fn field(k: *const Class, name: &str) -> Option<*mut Field> {
    let c = CString::new(name).ok()?;
    let mut cur = k;
    while !cur.is_null() {
        let f = (api().class_get_field_from_name)(cur, c.as_ptr());
        if !f.is_null() {
            return Some(f);
        }
        cur = (api().class_get_parent)(cur);
    }
    None
}

/// Calls a method; returns Err with the formatted managed exception if one was thrown.
pub unsafe fn invoke(m: *const Method, this: *mut c_void, args: &mut [*mut c_void]) -> Result<*mut Object, String> {
    let mut exc: *mut Object = std::ptr::null_mut();
    let r = (api().runtime_invoke)(m, this, args.as_mut_ptr(), &mut exc);
    if exc.is_null() {
        Ok(r)
    } else {
        let mut buf = vec![0u8; 4096];
        (api().format_exception)(exc, buf.as_mut_ptr() as _, buf.len() as i32);
        let mut msg = cstr(buf.as_ptr() as _);
        // the managed stack trace says where in the game it failed
        if let Some(st) = method((api().object_get_class)(exc), "get_StackTrace", 0) {
            let mut e2: *mut Object = std::ptr::null_mut();
            let s = (api().runtime_invoke)(st, exc as *mut c_void, std::ptr::null_mut(), &mut e2);
            if e2.is_null() && !s.is_null() {
                msg.push_str(" | at ");
                msg.push_str(&read_string(s).replace('\n', " | "));
            }
        }
        Err(msg)
    }
}

pub unsafe fn string(s: &str) -> *mut Object {
    (api().string_new_len)(s.as_ptr() as _, s.len() as u32)
}

pub unsafe fn read_string(o: *mut Object) -> String {
    if o.is_null() {
        return String::new();
    }
    let n = (api().string_length)(o) as usize;
    String::from_utf16_lossy(std::slice::from_raw_parts((api().string_chars)(o), n))
}

/// Pointer to the first element of a managed single-dimension array.
/// IL2CPP array layout on x64: object header (16) + bounds ptr (8) + length (8) = 32.
pub unsafe fn array_data<T>(arr: *mut Object) -> *mut T {
    (arr as *mut u8).add(32) as *mut T
}

pub unsafe fn unbox<T: Copy>(o: *mut Object) -> T {
    *((api().object_unbox)(o) as *const T)
}

/// Native function behind an internal call, e.g. "UnityEngine.RenderSettings::set_fogDensity".
/// Reaches engine setters whose managed wrappers were stripped from the build.
pub unsafe fn icall(name: &str) -> Option<*mut c_void> {
    let c = CString::new(name).ok()?;
    let p = (api().resolve_icall)(c.as_ptr());
    (!p.is_null()).then_some(p)
}
