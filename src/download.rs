//! Pinned, hash-checked downloads shared by the Phonon model, its voices and the chat models.

use std::io::{Read, Write};
use std::path::Path;
use std::time::Duration;

use sha2::{Digest, Sha256};

/// One file of a pinned model, with the size and SHA-256 it must have.
pub struct ModelFile {
    pub name: &'static str,
    pub size: u64,
    pub sha256: &'static str,
}

pub fn agent() -> ureq::Agent {
    ureq::Agent::config_builder()
        .timeout_connect(Some(Duration::from_secs(15)))
        .user_agent(concat!("DynamiteTTS/", env!("CARGO_PKG_VERSION")))
        .build()
        .into()
}

pub fn sha256_file(path: &Path) -> std::io::Result<String> {
    let mut file = std::fs::File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buf = vec![0u8; 1 << 16];
    loop {
        let n = file.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
    }
    Ok(hasher.finalize().iter().map(|b| format!("{b:02x}")).collect())
}

/// True when `path` has the pinned size and hash.
pub fn is_intact(path: &Path, file: &ModelFile) -> bool {
    std::fs::metadata(path).is_ok_and(|m| m.len() == file.size) && sha256_file(path).is_ok_and(|h| h == file.sha256)
}

/// Downloads `url` to `target` through a temporary file, checking size and hash when given.
/// `progress` gets the bytes received so far. A failed or cancelled download leaves nothing behind.
pub fn download(
    url: &str,
    target: &Path,
    size: Option<u64>,
    sha256: Option<&str>,
    cancelled: &dyn Fn() -> bool,
    progress: &mut dyn FnMut(u64),
) -> Result<(), String> {
    let name = target.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let response = agent().get(url).call().map_err(|e| format!("Could not download {name}: {e}"))?;
    let tmp = target.with_extension("part");
    let result = (|| {
        let mut reader = response.into_body().into_reader();
        let mut out = std::fs::File::create(&tmp).map_err(|e| format!("Could not write {name}: {e}"))?;
        let mut buf = vec![0u8; 1 << 16];
        let mut received = 0u64;
        loop {
            if cancelled() {
                return Err("cancelled".to_string());
            }
            let n = reader.read(&mut buf).map_err(|e| format!("Download of {name} failed: {e}"))?;
            if n == 0 {
                break;
            }
            out.write_all(&buf[..n]).map_err(|e| format!("Could not write {name}: {e}"))?;
            received += n as u64;
            progress(received);
        }
        drop(out);
        if size.is_some_and(|s| s != received) {
            return Err(format!("{name} downloaded with the wrong size."));
        }
        if let Some(expected) = sha256 {
            let actual = sha256_file(&tmp).map_err(|e| e.to_string())?;
            if !actual.eq_ignore_ascii_case(expected) {
                return Err(format!("{name} failed its checksum."));
            }
        }
        std::fs::rename(&tmp, target).map_err(|e| format!("Could not save {name}: {e}"))
    })();
    if result.is_err() {
        let _ = std::fs::remove_file(&tmp);
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hashes_files() {
        let path = std::env::temp_dir().join(format!("dt-hash-{}", std::process::id()));
        std::fs::write(&path, b"abc").unwrap();
        assert_eq!(sha256_file(&path).unwrap(), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        let file = ModelFile {
            name: "abc",
            size: 3,
            sha256: "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
        };
        assert!(is_intact(&path, &file));
        assert!(!is_intact(&path, &ModelFile { size: 4, ..file }));
        let _ = std::fs::remove_file(path);
    }
}
