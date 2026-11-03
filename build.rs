// Stamp the ptts version for the diagnostics log, and give the Windows executable its icon.
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
    println!("cargo:rustc-env=DT_PTTS_VERSION={version}");
    println!("cargo:rerun-if-changed=Cargo.lock");
    println!("cargo:rerun-if-changed=assets/icons/app.ico");

    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        let mut res = winresource::WindowsResource::new();
        res.set_icon("assets/icons/app.ico");
        res.set("ProductName", "Dynamite TTS");
        res.set("FileDescription", "Dynamite TTS");
        if let Err(e) = res.compile() {
            println!("cargo:warning=could not embed the app icon: {e}");
        }
    }
}
