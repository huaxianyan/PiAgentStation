// Windows 上不要弹控制台窗口。
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::fs;
use std::path::PathBuf;

use serde_json::{json, Value};
use tauri::{Manager, WebviewUrl, WebviewWindow, WebviewWindowBuilder, WindowEvent};

/// 壳自己的东西都放这个目录：中心地址、令牌、窗口位置、日志。
fn data_dir() -> PathBuf {
    let base = std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".to_string());
    PathBuf::from(base).join("SevenStation")
}

fn log(message: &str) {
    let dir = data_dir().join("logs");
    let _ = fs::create_dir_all(&dir);
    let path = dir.join("client.log");
    if fs::metadata(&path).map(|m| m.len() > 1_048_576).unwrap_or(false) {
        let _ = fs::remove_file(&path);
    }
    let line = format!("{} {}\n", timestamp(), message);
    use std::io::Write;
    if let Ok(mut file) = fs::OpenOptions::new().create(true).append(true).open(&path) {
        let _ = file.write_all(line.as_bytes());
    }
}

fn timestamp() -> String {
    let now = time::OffsetDateTime::now_local()
        .unwrap_or_else(|_| time::OffsetDateTime::now_utc());
    let format = time::macros::format_description!(
        "[year]-[month]-[day] [hour]:[minute]:[second]"
    );
    now.format(&format).unwrap_or_default()
}

/// 中心地址的来源，按优先级：
/// 1. 安装时写进注册表的地址与令牌（整机一份，`HKLM\SOFTWARE\SevenStation`）
/// 2. `%LOCALAPPDATA%\SevenStation\center.json`（本机开发或手工改过的那份）
/// 3. `token.txt`（同一目录，中心在本机时直接用它的令牌）
/// 4. 环境变量 `SEVENSTATION_TOKEN`
///
/// 装给整台机器的客户端靠第 1 条：所有用户共用一份配对信息，不用各自再填。
struct Center {
    base_url: String,
    token: Option<String>,
}

fn registry_center() -> Option<Center> {
    use winreg::enums::HKEY_LOCAL_MACHINE;
    use winreg::RegKey;

    let key = RegKey::predef(HKEY_LOCAL_MACHINE)
        .open_subkey(r"SOFTWARE\SevenStation")
        .ok()?;
    let url: String = key.get_value("CenterUrl").ok()?;
    if url.trim().is_empty() {
        return None;
    }

    let token: Option<String> = key
        .get_value::<String, _>("CenterToken")
        .ok()
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty());

    Some(Center {
        base_url: url.trim().to_string(),
        token,
    })
}

fn read_center() -> Center {
    let dir = data_dir();
    let config = dir.join("center.json");
    let fallback = "http://127.0.0.1:8787/".to_string();

    if let Some(mut installed) = registry_center() {
        if !installed.base_url.ends_with('/') {
            installed.base_url.push('/');
        }
        log(&format!("中心地址来自安装配置：{}", installed.base_url));
        return installed;
    }

    let mut base_url = fallback.clone();
    let mut token: Option<String> = None;

    if !config.exists() {
        let _ = fs::create_dir_all(&dir);
        let _ = fs::write(
            &config,
            "{\n  \"url\": \"http://127.0.0.1:8787/\",\n  \"token\": \"\"\n}\n",
        );
    } else if let Ok(text) = fs::read_to_string(&config) {
        if let Ok(parsed) = serde_json::from_str::<Value>(&text) {
            if let Some(url) = parsed.get("url").and_then(|v| v.as_str()) {
                if !url.trim().is_empty() {
                    base_url = url.trim().to_string();
                }
            }
            if let Some(value) = parsed.get("token").and_then(|v| v.as_str()) {
                let trimmed = value.trim();
                if !trimmed.is_empty() {
                    token = Some(trimmed.to_string());
                }
            }
        }
    }

    if !base_url.ends_with('/') {
        base_url.push('/');
    }

    if token.is_none() {
        if let Ok(value) = fs::read_to_string(dir.join("token.txt")) {
            let trimmed = value.trim();
            if trimmed.len() >= 16 {
                token = Some(trimmed.to_string());
            }
        }
    }

    if token.is_none() {
        if let Ok(value) = std::env::var("SEVENSTATION_TOKEN") {
            let trimmed = value.trim();
            if !trimmed.is_empty() {
                token = Some(trimmed.to_string());
            }
        }
    }

    Center { base_url, token }
}

fn center_url(center: &Center) -> String {
    match &center.token {
        None => center.base_url.clone(),
        Some(token) => {
            let separator = if center.base_url.contains('?') { '&' } else { '?' };
            format!(
                "{}{}token={}",
                center.base_url,
                separator,
                urlencode(token)
            )
        }
    }
}

fn urlencode(value: &str) -> String {
    let mut out = String::new();
    for byte in value.bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                out.push(byte as char)
            }
            _ => out.push_str(&format!("%{byte:02X}")),
        }
    }
    out
}

/// 中心跟壳是不是同一台机器：决定选目录时该给谁的路径。
fn center_is_local(center: &Center) -> bool {
    let host = center
        .base_url
        .split("//")
        .nth(1)
        .unwrap_or_default()
        .split(['/', ':'])
        .next()
        .unwrap_or_default()
        .to_lowercase();

    host == "127.0.0.1"
        || host == "localhost"
        || host == "::1"
        || host == std::env::var("COMPUTERNAME").unwrap_or_default().to_lowercase()
}

fn bounds_path() -> PathBuf {
    data_dir().join("window.json")
}

fn read_bounds() -> Option<(f64, f64, f64, f64)> {
    let text = fs::read_to_string(bounds_path()).ok()?;
    let parsed: Value = serde_json::from_str(&text).ok()?;
    let get = |key: &str| parsed.get(key).and_then(|v| v.as_f64());
    Some((get("left")?, get("top")?, get("width")?, get("height")?))
}

fn save_bounds(window: &WebviewWindow) {
    let (Ok(position), Ok(size)) = (window.outer_position(), window.inner_size()) else {
        return;
    };
    let scale = window.scale_factor().unwrap_or(1.0);
    let payload = json!({
        "left": position.x as f64 / scale,
        "top": position.y as f64 / scale,
        "width": size.width as f64 / scale,
        "height": size.height as f64 / scale,
    });
    let _ = fs::create_dir_all(data_dir());
    let _ = fs::write(
        bounds_path(),
        serde_json::to_string_pretty(&payload).unwrap_or_default(),
    );
}

/// 选目录：交给系统的对话框，选完把路径给页面。
#[tauri::command]
async fn pick_folder(app: tauri::AppHandle, initial: Option<String>) -> Option<String> {
    use tauri_plugin_dialog::DialogExt;

    let mut builder = app.dialog().file();
    if let Some(path) = initial.filter(|p| !p.trim().is_empty()) {
        builder = builder.set_directory(path);
    }

    builder
        .blocking_pick_folder()
        .map(|picked| picked.to_string())
}

/// 页面问壳：中心在本机吗。
#[tauri::command]
fn center_local(app: tauri::AppHandle) -> bool {
    let _ = app;
    center_is_local(&read_center())
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            // 已经开着一个了：把那个窗口提到前面，不再开第二个。
            if let Some(window) = app.get_webview_window("main") {
                let _ = window.unminimize();
                let _ = window.set_focus();
            }
        }))
        .plugin(tauri_plugin_dialog::init())
        .invoke_handler(tauri::generate_handler![pick_folder, center_local])
        .setup(|app| {
            let center = read_center();
            log(&format!("启动，中心 {}", center.base_url));

            let mut builder = WebviewWindowBuilder::new(
                app,
                "main",
                WebviewUrl::App("index.html".into()),
            )
            .title("Seven Station")
            .inner_size(1240.0, 820.0)
            .min_inner_size(880.0, 560.0);

            if let Some((left, top, width, height)) = read_bounds() {
                builder = builder.position(left, top).inner_size(width, height);
            }

            let window = builder.build()?;

            let handle = window.clone();
            window.on_window_event(move |event| match event {
                WindowEvent::Moved(_) | WindowEvent::Resized(_) => save_bounds(&handle),
                WindowEvent::CloseRequested { .. } => save_bounds(&handle),
                _ => {}
            });

            let target = center_url(&center);
            let navigator = window.clone();
            tauri::async_runtime::spawn(async move {
                // 等页面把壳的消息通道准备好再跳，否则页面拿不到 __TAURI__。
                std::thread::sleep(std::time::Duration::from_millis(150));
                match tauri::Url::parse(&target) {
                    Ok(url) => {
                        if let Err(error) = navigator.navigate(url) {
                            log(&format!("打不开中心页面：{error}"));
                            let _ = navigator.eval(format!(
                                "window.sevenStationShowState && window.sevenStationShowState('连不上中心，检查一下服务是否在跑')"
                            ));
                        }
                    }
                    Err(error) => log(&format!("中心地址不对：{error}")),
                }
            });

            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("Seven Station 启动失败");
}
