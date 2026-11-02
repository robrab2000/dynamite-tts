// Stamp the ptts version the DLL was built against, for the app's diagnostics log.
fn main() {
    let lock = std::fs::read_to_string("Cargo.lock").unwrap_or_default();
    let mut version = "unknown".to_string();
    let mut in_ptts = false;
    for line in lock.lines() {
        if line.starts_with("name = ") {
            in_ptts = line == "name = \"ptts\"";
        } else if in_ptts {
            if let Some(v) = line.strip_prefix("version = ") {
                version = v.trim_matches('"').to_string();
                break;
            }
        }
    }
    println!("cargo:rustc-env=DP_PTTS_VERSION={version}");
    println!("cargo:rerun-if-changed=Cargo.lock");
}
