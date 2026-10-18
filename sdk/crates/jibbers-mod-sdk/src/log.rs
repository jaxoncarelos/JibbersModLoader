//! Append-only log next to the game exe (`jibbers_hotload.log`).

use std::io::Write;
use std::sync::Mutex;

static LOCK: Mutex<()> = Mutex::new(());

pub fn info(msg: impl AsRef<str>) {
    let _g = LOCK.lock();
    let path = crate::game_dir().join("jibbers_hotload.log");
    if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(path) {
        let t = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default();
        let _ = writeln!(f, "[{}.{:03}] {}", t.as_secs(), t.subsec_millis(), msg.as_ref());
    }
}
