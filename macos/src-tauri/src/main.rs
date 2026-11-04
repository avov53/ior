#[tauri::command]
fn connection_status() -> serde_json::Value {
    serde_json::json!({"connected":false,"vpnAvailable":false,"code":"MACOS_TUN_NOT_IMPLEMENTED"})
}
fn main() {
    tauri::Builder::default().invoke_handler(tauri::generate_handler![connection_status])
        .run(tauri::generate_context!()).expect("ior macOS startup failed");
}
