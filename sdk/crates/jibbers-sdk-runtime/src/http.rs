//! Minimal HTTPS GET over WinHTTP (ships with Windows; uses the system proxy settings and
//! certificate store, and adds no dependencies). HTTPS only; responses are size-capped.

use crate::update::Fetch;
use std::ffi::c_void;
use std::ptr::{null, null_mut};
use windows_sys::Win32::Networking::WinHttp::*;

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(Some(0)).collect()
}

struct Handle(*mut c_void);
impl Drop for Handle {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe { WinHttpCloseHandle(self.0) };
        }
    }
}

/// `https://host[:port]/path?query` split into its parts.
pub fn split_url(url: &str) -> Result<(String, u16, String), String> {
    let rest = url.strip_prefix("https://").ok_or("only https:// URLs are allowed")?;
    let (authority, path) = match rest.find('/') {
        Some(i) => (&rest[..i], &rest[i..]),
        None => (rest, "/"),
    };
    if authority.is_empty() || authority.contains('@') || authority.contains(char::is_whitespace) {
        return Err(format!("bad URL {url:?}"));
    }
    let (host, port) = match authority.rsplit_once(':') {
        Some((h, p)) => (h, p.parse::<u16>().map_err(|_| format!("bad port in {url:?}"))?),
        None => (authority, 443),
    };
    Ok((host.to_string(), port, path.to_string()))
}

/// Downloads `url`, failing on any status but 200 or on more than `max` bytes.
pub fn get(url: &str, max: usize) -> Result<Vec<u8>, String> {
    let (host, port, path) = split_url(url)?;
    unsafe {
        let agent = wide("JibbersSdk");
        // Automatic proxy discovery needs Windows 8.1+; fall back to the default proxy.
        let mut session = Handle(WinHttpOpen(agent.as_ptr(), WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, null(), null(), 0));
        if session.0.is_null() {
            session = Handle(WinHttpOpen(agent.as_ptr(), WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, null(), null(), 0));
        }
        if session.0.is_null() {
            return Err("WinHttpOpen failed".into());
        }
        // resolve, connect, send, receive (ms): a dead network must never hang the game.
        WinHttpSetTimeouts(session.0, 5_000, 8_000, 15_000, 20_000);
        let connect = Handle(WinHttpConnect(session.0, wide(&host).as_ptr(), port, 0));
        if connect.0.is_null() {
            return Err(format!("could not connect to {host}"));
        }
        let request = Handle(WinHttpOpenRequest(connect.0, wide("GET").as_ptr(), wide(&path).as_ptr(), null(), null(), null(), WINHTTP_FLAG_SECURE));
        if request.0.is_null() {
            return Err("WinHttpOpenRequest failed".into());
        }
        if WinHttpSendRequest(request.0, null(), 0, null(), 0, 0, 0) == 0 || WinHttpReceiveResponse(request.0, null_mut()) == 0 {
            return Err(format!("request to {host} failed (offline or blocked?)"));
        }
        let mut status = 0u32;
        let mut size = std::mem::size_of::<u32>() as u32;
        if WinHttpQueryHeaders(request.0, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, null(), &mut status as *mut u32 as *mut c_void, &mut size, null_mut()) == 0 {
            return Err("no HTTP status".into());
        }
        if status != 200 {
            return Err(format!("HTTP {status} from {host}"));
        }
        let mut body = Vec::new();
        loop {
            let mut avail = 0u32;
            if WinHttpQueryDataAvailable(request.0, &mut avail) == 0 {
                return Err("download interrupted".into());
            }
            if avail == 0 {
                break;
            }
            if body.len() + avail as usize > max {
                return Err(format!("response larger than {max} bytes"));
            }
            let at = body.len();
            body.resize(at + avail as usize, 0);
            let mut read = 0u32;
            if WinHttpReadData(request.0, body[at..].as_mut_ptr() as *mut c_void, avail, &mut read) == 0 {
                return Err("download interrupted".into());
            }
            body.truncate(at + read as usize);
        }
        Ok(body)
    }
}

/// The real network.
pub struct WinHttp;

impl Fetch for WinHttp {
    fn get(&self, url: &str, max: usize) -> Result<Vec<u8>, String> {
        get(url, max)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn urls_are_split_and_restricted() {
        assert_eq!(split_url("https://github.com/o/r/releases/latest/download/manifest.json").unwrap(), ("github.com".into(), 443, "/o/r/releases/latest/download/manifest.json".into()));
        assert_eq!(split_url("https://example.com").unwrap(), ("example.com".into(), 443, "/".into()));
        assert_eq!(split_url("https://example.com:8443/x").unwrap().1, 8443);
        assert!(split_url("http://example.com/x").is_err());
        assert!(split_url("ftp://example.com").is_err());
        assert!(split_url("https://user@example.com/").is_err());
        assert!(split_url("https:///x").is_err());
        assert!(split_url("https://exa mple.com/").is_err());
        assert!(split_url("https://example.com:99999/").is_err());
    }

    /// Needs the network: `cargo test -p jibbers-sdk-runtime -- --ignored`.
    #[test]
    #[ignore]
    fn fetches_over_https() {
        let body = get("https://example.com/", 1 << 20).unwrap();
        assert!(String::from_utf8_lossy(&body).contains("Example Domain"));
        assert!(get("https://example.com/definitely-missing-404", 1 << 20).is_err());
        assert!(get("https://example.com/", 100).is_err(), "size cap");
    }
}
