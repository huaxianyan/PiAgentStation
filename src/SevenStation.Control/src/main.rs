// Windows 上不要弹控制台窗口。
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod core;

use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{Emitter, Manager};

/// 托盘里那两项要跟着状态改文字，所以留着句柄。
struct TrayMenu {
    headline: MenuItem<tauri::Wry>,
    toggle: MenuItem<tauri::Wry>,
}

#[tauri::command]
fn read_state() -> core::Snapshot {
    core::snapshot()
}

#[tauri::command]
fn control(app: tauri::AppHandle, action: String) -> Result<(), String> {
    match action.as_str() {
        "start" => core::start(),
        "stop" => core::stop(),
        "restart" => core::restart(),
        _ => Err("不认识的操作。".into()),
    }?;

    publish(&app);
    Ok(())
}

#[tauri::command]
fn set_autostart(app: tauri::AppHandle, on: bool) -> Result<(), String> {
    core::set_autostart(on)?;
    publish(&app);
    Ok(())
}

#[tauri::command]
fn set_lan(app: tauri::AppHandle, on: bool) -> Result<(), String> {
    core::set_lan(on)?;
    publish(&app);
    Ok(())
}

#[tauri::command]
fn regenerate_token(app: tauri::AppHandle) -> Result<(), String> {
    core::regenerate_token()?;
    publish(&app);
    let _ = app.emit("toast", "新令牌已生效，其他设备要用它重新连一次。".to_string());
    Ok(())
}

#[tauri::command]
fn copy_text(text: String) -> Result<(), String> {
    core::copy_text(&text)
}

#[tauri::command]
fn open_target(target: String) -> Result<(), String> {
    core::open_target(&target)
}

/// 状态变了就往页面推一次，同时把托盘上的文字换掉。
fn publish(app: &tauri::AppHandle) {
    let snapshot = core::snapshot();
    let _ = app.emit("state", &snapshot);

    if let Some(menu) = app.try_state::<TrayMenu>() {
        let _ = menu.headline.set_text(if snapshot.running {
            format!("正在运行 · {}", snapshot.listen)
        } else {
            "已停止".to_string()
        });
        let _ = menu.toggle.set_text(if snapshot.running { "停止中心" } else { "启动中心" });
    }

    if let Some(tray) = app.tray_by_id("main") {
        let _ = tray.set_tooltip(Some(if snapshot.running {
            "Seven Station 中心 · 正在运行"
        } else {
            "Seven Station 中心 · 已停止"
        }));
    }
}

fn show_window(app: &tauri::AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.unminimize();
        let _ = window.show();
        let _ = window.set_focus();
    }
}

fn build_tray(app: &tauri::App) -> tauri::Result<()> {
    let headline = MenuItem::with_id(app, "headline", "正在读取状态…", false, None::<&str>)?;
    let open = MenuItem::with_id(app, "open", "打开控制台", true, None::<&str>)?;
    let toggle = MenuItem::with_id(app, "toggle", "启动中心", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "退出", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&headline, &open, &toggle, &quit])?;

    app.manage(TrayMenu {
        headline: headline.clone(),
        toggle: toggle.clone(),
    });

    let mut builder = TrayIconBuilder::with_id("main")
        .tooltip("Seven Station 中心")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id().as_ref() {
            "open" => show_window(app),
            "toggle" => {
                let running = core::snapshot().running;
                let result = if running { core::stop() } else { core::start() };
                match result {
                    Ok(()) => publish(app),
                    Err(message) => {
                        show_window(app);
                        let _ = app.emit("toast", message);
                    }
                }
            }
            "quit" => app.exit(0),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } = event
            {
                show_window(tray.app_handle());
            }
        });

    // 托盘用浅底那张：客户端托盘是深底，两个放一起才能一眼分开。
    let tray_png = tauri::image::Image::from_bytes(include_bytes!("../icons/tray.png"))?;
    builder = builder.icon(tray_png);

    builder.build(app)?;
    Ok(())
}

fn main() {
    // 提权子进程只做一件事就退，别建窗口、别起 WebView。
    let args: Vec<String> = std::env::args().collect();
    if args.len() >= 3 && args[1] == core::ELEVATE_FLAG {
        std::process::exit(core::run_elevated(&args[2..]));
    }

    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            show_window(app);
        }))
        .invoke_handler(tauri::generate_handler![
            read_state,
            control,
            set_autostart,
            set_lan,
            regenerate_token,
            copy_text,
            open_target
        ])
        .setup(|app| {
            let window = tauri::WebviewWindowBuilder::new(
                app,
                "main",
                tauri::WebviewUrl::App("index.html".into()),
            )
            .title("Seven Station 中心")
            .inner_size(720.0, 626.0)
            .min_inner_size(620.0, 560.0)
            .center()
            .build()?;

            // 关窗口只是收进托盘，中心照常跑；真退出走托盘右键。
            let handle = window.clone();
            window.on_window_event(move |event| {
                if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                    api.prevent_close();
                    let _ = handle.hide();
                }
            });

            build_tray(app)?;

            // 每 3 秒把状态推一次；服务和中心都可能被别处改动。
            let poller = app.handle().clone();
            std::thread::spawn(move || loop {
                std::thread::sleep(std::time::Duration::from_secs(3));
                publish(&poller);
            });

            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("控制台启动失败");
}
