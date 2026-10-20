//! Signed SDK-to-bootstrap migration. Never replaces the game's loaded proxy until its process exits.
#![allow(dead_code)]
mod manifest {
    pub const MAX_FILE_BYTES: u64 = 32 * 1024 * 1024;
    pub fn parse_version(s: &str) -> Option<(u64, u64, u64)> { jibbers_distribution::catalog::version(s).ok() }
    pub fn sha256_hex(bytes: &[u8]) -> String { jibbers_distribution::catalog::hash(bytes) }
    pub fn verify_signature(keys: &[[u8; 32]], bytes: &[u8], sig: &str) -> Result<(), String> { jibbers_distribution::catalog::verify_signature(bytes, sig, keys) }
}
#[path = "../../../sdk/crates/jibbers-sdk-runtime/src/loader_migration.rs"]
mod migration;

fn run() -> Result<(), String> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if args.len() != 4 || args[0] != "--game" || args[2] != "--wait-pid" { return Err("expected --game <folder> --wait-pid <game pid>".into()); }
    let game = std::fs::canonicalize(&args[1]).map_err(|e| e.to_string())?;
    let pid: u32 = args[3].parse().map_err(|_| "invalid game pid")?;
    if pid == 0 || pid == std::process::id() { return Err("invalid game pid".into()); }
    // Verify before waiting and again after exit, because an SDK update could have changed the
    // staged files during this session. Invalid or incomplete payloads never touch version.dll.
    migration::verify(&game, jibbers_distribution::trust::KEYS)?;
    unsafe {
        use windows_sys::Win32::{Foundation::{CloseHandle, GetLastError, ERROR_INVALID_PARAMETER, WAIT_OBJECT_0}, System::Threading::{OpenProcess, WaitForSingleObject, PROCESS_SYNCHRONIZE}};
        let process = OpenProcess(PROCESS_SYNCHRONIZE, 0, pid);
        if process.is_null() {
            if GetLastError() != ERROR_INVALID_PARAMETER { return Err("cannot wait for the launching game; migration cancelled".into()); }
        } else {
            let waited = WaitForSingleObject(process, u32::MAX);
            CloseHandle(process);
            if waited != WAIT_OBJECT_0 { return Err("waiting for the game failed; migration cancelled".into()); }
        }
    }
    let result = migration::apply(&game, jibbers_distribution::trust::KEYS);
    use std::io::Write;
    if let Ok(mut log) = std::fs::OpenOptions::new().create(true).append(true).open(game.join("jibbers_hotload.log")) {
        let _ = writeln!(log, "[SDK loader migration] {}", match &result { Ok(true) => "new version.dll and phone UI installed; ready for next launch".to_string(), Ok(false) => "already installed".to_string(), Err(error) => format!("not applied: {error}") });
    }
    result.map(|_| ())
}
fn main() { if let Err(error) = run() { eprintln!("{error}"); std::process::exit(1); } }
