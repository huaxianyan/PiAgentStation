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

/// 中心地址与令牌分头找，各自取第一个有值的：
/// 地址：安装时写进注册表的（整机一份）→ `center.json` → 默认本机
/// 令牌：`center.json` → 注册表 → `token.txt` → 环境变量 `SEVENSTATION_TOKEN`
///
/// 令牌优先看 `center.json`：安装时没填令牌的情况很常见，用户自己补一份就生效，
/// 不用为了改一个字符串重装一遍。
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

/// 本机保存的那份配置：`userSet` 为真表示用户自己在界面上改过，优先级高于安装时写进注册表的值。
fn stored_center() -> Option<Value> {
    let text = fs::read_to_string(data_dir().join("center.json")).ok()?;
    serde_json::from_str::<Value>(&text).ok()
}

fn stored_text(config: &Option<Value>, key: &str) -> Option<String> {
    let value = config.as_ref()?.get(key)?.as_str()?.trim().to_string();
    if value.is_empty() {
        None
    } else {
        Some(value)
    }
}

fn read_center() -> Center {
    let dir = data_dir();
    let config_path = dir.join("center.json");
    let fallback = "http://127.0.0.1:8787/".to_string();

    if !config_path.exists() {
        let _ = fs::create_dir_all(&dir);
        let _ = fs::write(
            &config_path,
            "{\n  \"url\": \"http://127.0.0.1:8787/\",\n  \"token\": \"\"\n}\n",
        );
    }

    let stored = stored_center();
    let user_set = stored
        .as_ref()
        .and_then(|value| value.get("userSet"))
        .and_then(|value| value.as_bool())
        .unwrap_or(false);
    let installed = registry_center();

    let mut base_url: Option<String> = None;
    let mut token: Option<String> = None;

    if user_set {
        base_url = stored_text(&stored, "url");
        token = stored_text(&stored, "token");
        log("连接设置来自本机保存的配置");
    }

    if base_url.is_none() {
        if let Some(center) = &installed {
            base_url = Some(center.base_url.clone());
            log(&format!("中心地址来自安装配置：{}", center.base_url));
        }
    }

    if base_url.is_none() {
        base_url = stored_text(&stored, "url");
    }

    if token.is_none() {
        if let Some(center) = &installed {
            token = center.token.clone();
        }
    }

    if token.is_none() {
        token = stored_text(&stored, "token");
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

    let mut base_url = base_url.unwrap_or(fallback);
    if !base_url.ends_with('/') {
        base_url.push('/');
    }

    Center { base_url, token }
}

/// 界面上读取这台设备的连接设置。
#[tauri::command]
fn client_config_get() -> Value {
    let stored = stored_center();
    let user_set = stored
        .as_ref()
        .and_then(|value| value.get("userSet"))
        .and_then(|value| value.as_bool())
        .unwrap_or(false);
    let installed = registry_center();
    let center = read_center();

    let source = if user_set {
        "file"
    } else if installed.is_some() {
        "installed"
    } else {
        "default"
    };

    serde_json::json!({
        "url": center.base_url,
        "token": center.token.unwrap_or_default(),
        "source": source,
    })
}

/// 界面上保存连接设置：写到本机 `center.json`（带 userSet 标记），然后直接连新的地址。
#[tauri::command]
fn client_config_save(window: tauri::WebviewWindow, url: String, token: String) -> Result<(), String> {
    let mut base_url = url.trim().to_string();
    if base_url.is_empty() {
        return Err("中心地址不能为空".into());
    }
    if !base_url.starts_with("http://") && !base_url.starts_with("https://") {
        return Err("地址要以 http:// 或 https:// 开头".into());
    }
    if !base_url.ends_with('/') {
        base_url.push('/');
    }

    let dir = data_dir();
    fs::create_dir_all(&dir).map_err(|error| format!("建目录失败：{error}"))?;
    let payload = serde_json::json!({
        "url": base_url,
        "token": token.trim(),
        "userSet": true,
    });
    let text = serde_json::to_string_pretty(&payload).map_err(|error| format!("写配置失败：{error}"))?;
    fs::write(dir.join("center.json"), text).map_err(|error| format!("写配置失败：{error}"))?;
    log(&format!("本机连接设置已更新：{base_url}"));

    let mut target = base_url;
    if let Some(value) = payload.get("token").and_then(|value| value.as_str()) {
        if !value.is_empty() {
            let encoded: String = value
                .chars()
                .map(|ch| {
                    if ch.is_ascii_alphanumeric() || "-_.~".contains(ch) {
                        ch.to_string()
                    } else {
                        format!("%{:02X}", ch as u32)
                    }
                })
                .collect();
            target.push_str(&format!("?token={encoded}"));
        }
    }

    let parsed = tauri::Url::parse(&target).map_err(|error| format!("地址解析失败：{error}"))?;
    window.navigate(parsed).map_err(|error| format!("打开新地址失败：{error}"))?;
    Ok(())
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
        .invoke_handler(tauri::generate_handler![
            pick_folder,
            center_local,
            client_config_get,
            client_config_save
        ])
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
