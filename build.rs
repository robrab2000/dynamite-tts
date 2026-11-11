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

    // llama.cpp's Metal code checks the macOS version with @available when it is built for an older
    // macOS than the SDK. That check lives in clang's runtime library, which rustc doesn't link.
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("macos") {
        let dir = std::process::Command::new("xcrun").args(["clang", "--print-resource-dir"]).output();
        match dir {
            Ok(out) if out.status.success() => {
                let dir = String::from_utf8_lossy(&out.stdout).trim().to_string();
                println!("cargo:rustc-link-search=native={dir}/lib/darwin");
                println!("cargo:rustc-link-lib=static=clang_rt.osx");
            }
            _ => println!("cargo:warning=could not find clang's runtime library"),
        }
    }

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
