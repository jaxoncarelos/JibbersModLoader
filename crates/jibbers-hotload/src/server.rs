//! Localhost command server: one JSON request per line in, one JSON reply per line out.
//! Bound to 127.0.0.1 only, so nothing outside this PC can reach it.
//!
//! The loader answers `ping` and `reload-core` itself; everything else goes to the core.

use crate::{core, log};
use serde_json::{json, Value};
use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};

pub const PORT: u16 = 47820;

pub fn serve() {
    let listener = match TcpListener::bind(("127.0.0.1", PORT)) {
        Ok(l) => l,
        Err(e) => {
            log::info(format!("server: cannot listen on {PORT}: {e}"));
            return;
        }
    };
    log::info(format!("server: listening on 127.0.0.1:{PORT}"));
    for conn in listener.incoming().flatten() {
        std::thread::spawn(move || handle(conn));
    }
}

fn handle(mut conn: TcpStream) {
    if let Err(e) = crate::ensure_ready() {
        let _ = conn.write_all(json!({"ok": false, "error": e}).to_string().as_bytes());
        let _ = conn.write_all(b"
");
        return;
    }
    // Register with the IL2CPP GC for the life of the connection: a request may reach game code,
    // and an unregistered thread touching the heap during a collection aborts the game.
    let a = crate::il2cpp::api();
    let thread = unsafe { (a.thread_attach)((a.domain_get)()) };
    serve_conn(conn);
    unsafe { (a.thread_detach)(thread) };
}

fn serve_conn(conn: TcpStream) {
    let Ok(read) = conn.try_clone() else { return };
    let mut out = conn;
    for line in BufReader::new(read).lines() {
        let Ok(line) = line else { break };
        if line.trim().is_empty() {
            continue;
        }
        let reply = reply_to(line.as_bytes());
        if out.write_all(&reply).and_then(|_| out.write_all(b"\n")).is_err() {
            break;
        }
    }
}

fn reply_to(line: &[u8]) -> Vec<u8> {
    let cmd = serde_json::from_slice::<Value>(line)
        .ok()
        .and_then(|v| v.get("cmd").and_then(Value::as_str).map(str::to_string))
        .unwrap_or_default();
    let r = match cmd.as_str() {
        "ping" => Ok(json!({
            "ok": true,
            "mod": "jibbers-hotload",
            "loader": env!("CARGO_PKG_VERSION"),
            "protocol": 1,
            "coreLoaded": core::loaded(),
        })
        .to_string()
        .into_bytes()),
        "reload-core" => core::reload().map(|_| json!({"ok": true}).to_string().into_bytes()),
        _ => core::handle(line),
    };
    r.unwrap_or_else(|e| json!({"ok": false, "error": e}).to_string().into_bytes())
}
