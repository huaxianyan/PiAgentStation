use std::fs;
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::PathBuf;
use std::os::windows::process::CommandExt;
use std::process::{Command, Stdio};

use serde::Serialize;

/// 服务名、防火墙规则名、中心可执行文件名，都只在这里出现一次。
pub const SERVICE: &str = "SevenStationCore";
pub const FIREWALL_RULE: &str = "Seven Station 中心";
pub const CORE_EXE: &str = "SevenStation.Core.exe";

/// 要提权才能做的事，把自己再拉起来一次，带上这个参数。
pub const ELEVATE_FLAG: &str = "--do";

const CREATE_NO_WINDOW: u32 = 0x0800_0000;
const ERROR_ACCESS_DENIED: i32 = 5;
const DEFAULT_LISTEN: &str = "http://127.0.0.1:8787";

#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    pub service_installed: bool,
    pub running: bool,
    pub mode: String,
    pub listen: String,
    pub token: String,
    pub data_directory: String,
    pub version: String,
    pub autostart: bool,
    pub scope: String,
    pub local_address: Option<String>,
    pub firewall_rule: bool,
    pub logs: Vec<String>,
    pub since: String,
}

// ------------------------------ 目录 ------------------------------

/// 中心根目录，`SEVENSTATION_CORE_HOME` 可整体挪走（开发和测试用它隔离）。
pub fn home() -> PathBuf {
    match std::env::var("SEVENSTATION_CORE_HOME") {
        Ok(value) if !value.trim().is_empty() => PathBuf::from(value.trim()),
        _ => PathBuf::from(std::env::var("ProgramData").unwrap_or_else(|_| "C:\\ProgramData".into()))
            .join("SevenStation.Core"),
    }
}

pub fn data_directory() -> PathBuf {
    match std::env::var("SEVENSTATION_DATA_DIR") {
        Ok(value) if !value.trim().is_empty() => PathBuf::from(value.trim()),
        _ => home().join("data"),
    }
}

fn settings_file() -> PathBuf {
    home().join("settings.json")
}

fn token_file() -> PathBuf {
    home().join("token.txt")
}

fn log_file() -> PathBuf {
    home().join("logs").join("center.log")
}

// ------------------------------ 状态快照 ------------------------------

pub fn snapshot() -> Snapshot {
    let installed = service_exists();
    let running = if installed { service_running() } else { portable_running() };
    let listen = read_listen();
    let port = port_of(&listen);

    Snapshot {
        service_installed: installed,
        running,
        mode: match (installed, running) {
            (true, true) => "service",
            (true, false) => "stopped",
            (false, true) => "portable",
            (false, false) => "stopped",
        }
        .to_string(),
        listen: listen.clone(),
        token: read_token(),
        data_directory: data_directory().display().to_string(),
        version: env!("CARGO_PKG_VERSION").to_string(),
        autostart: installed && autostart_on(),
        scope: if is_lan(&listen) { "lan" } else { "local" }.to_string(),
        local_address: local_address(),
        firewall_rule: has_firewall_rule(port),
        logs: tail_log(6),
        since: since_text(),
    }
}

// ------------------------------ 服务与进程 ------------------------------

fn run(program: &str, args: &[&str]) -> (i32, String) {
    let mut command = Command::new(program);
    command
        .args(args)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .creation_flags(CREATE_NO_WINDOW);

    match command.output() {
        Ok(output) => {
            let mut text = String::from_utf8_lossy(&output.stdout).into_owned();
            text.push_str(&String::from_utf8_lossy(&output.stderr));
            (output.status.code().unwrap_or(-1), text)
        }
        Err(_) => (-1, String::new()),
    }
}

fn service_exists() -> bool {
    run("sc.exe", &["query", SERVICE]).0 == 0
}

fn service_running() -> bool {
    let (code, text) = run("sc.exe", &["query", SERVICE]);
    code == 0 && text.contains("RUNNING")
}

fn autostart_on() -> bool {
    let (code, text) = run("sc.exe", &["qc", SERVICE]);
    code == 0 && text.contains("AUTO_START")
}

fn portable_running() -> bool {
    let (_, text) = run("tasklist.exe", &["/FI", &format!("IMAGENAME eq {CORE_EXE}"), "/NH"]);
    text.contains(CORE_EXE)
}

pub fn start() -> Result<(), String> {
    if service_exists() {
        let (code, _) = run("sc.exe", &["start", SERVICE]);
        return match code {
            0 => wait_for(true),
            ERROR_ACCESS_DENIED => elevate(&["start"]),
            _ => Err("中心没启动起来，可以在「事件查看器」里看服务为什么退出。".into()),
        };
    }

    let exe = core_exe_path()?;
    if !exe.exists() {
        return Err(format!("没找到 {CORE_EXE}，它应该和控制台放在同一个目录里。"));
    }

    Command::new(&exe)
        .current_dir(exe.parent().unwrap_or_else(|| std::path::Path::new(".")))
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .map_err(|error| format!("启动失败：{error}"))?;
    Ok(())
}

pub fn stop() -> Result<(), String> {
    if service_exists() {
        let (code, _) = run("sc.exe", &["stop", SERVICE]);
        return match code {
            0 => wait_for(false),
            ERROR_ACCESS_DENIED => elevate(&["stop"]),
            _ => Err("中心没停下来，稍后再试一次。".into()),
        };
    }

    if !portable_running() {
        return Ok(());
    }

    run("taskkill.exe", &["/IM", CORE_EXE, "/T", "/F"]);
    if portable_running() {
        return Err("中心没停下来，稍后再试一次。".into());
    }
    Ok(())
}

pub fn restart() -> Result<(), String> {
    if service_exists() {
        // 直接 sc stop + sc start 要两跳，提权时也只要一次 UAC，所以合并成一条。
        let (code, _) = run("sc.exe", &["stop", SERVICE]);
        match code {
            0 => {}
            ERROR_ACCESS_DENIED => return elevate(&["restart"]),
            _ => {}
        }
        wait_for(false)?;
        let (code, _) = run("sc.exe", &["start", SERVICE]);
        return match code {
            0 => wait_for(true),
            ERROR_ACCESS_DENIED => elevate(&["restart"]),
            _ => Err("中心没启动起来，可以在「事件查看器」里看服务为什么退出。".into()),
        };
    }

    stop()?;
    start()
}

/// `sc start/stop` 是异步的，等它真的换了状态再回界面。
fn wait_for(running: bool) -> Result<(), String> {
    for _ in 0..40 {
        if service_running() == running {
            return Ok(());
        }
        std::thread::sleep(std::time::Duration::from_millis(500));
    }

    Err(if running {
        "中心没启动起来，可以在「事件查看器」里看服务为什么退出。".to_string()
    } else {
        "中心没停下来，稍后再试一次。".to_string()
    })
}

fn core_exe_path() -> Result<PathBuf, String> {
    let current = std::env::current_exe().map_err(|error| format!("找不到自己的位置：{error}"))?;
    Ok(current
        .parent()
        .map(|dir| dir.join(CORE_EXE))
        .unwrap_or_else(|| PathBuf::from(CORE_EXE)))
}

// ------------------------------ 自启 ------------------------------

pub fn set_autostart(on: bool) -> Result<(), String> {
    if !service_exists() {
        return Err("还没装成服务，便携模式没有开机自启。".into());
    }

    let value = if on { "auto" } else { "demand" };
    let (code, _) = run("sc.exe", &["config", SERVICE, "start=", value]);
    match code {
        0 => Ok(()),
        ERROR_ACCESS_DENIED => elevate(&[if on { "autostart-on" } else { "autostart-off" }]),
        _ => Err("开机自启没改成，稍后再试一次。".into()),
    }
}

// ------------------------------ 监听范围与防火墙 ------------------------------

pub fn set_lan(on: bool) -> Result<(), String> {
    let port = port_of(&read_listen());
    let listen = if on {
        format!("http://0.0.0.0:{port}")
    } else {
        format!("http://127.0.0.1:{port}")
    };
    write_listen(&listen)?;

    if on && !has_firewall_rule(port) && add_firewall_rule(port).is_err() {
        // 防火墙没放行也要把监听改回去，不然局域网连不上还留着个开着的端口。
        write_listen(&format!("http://127.0.0.1:{port}"))?;
        return Err("没在防火墙里放行，还是只允许本机连接。".into());
    }

    restart()
}

fn add_firewall_rule(port: u16) -> Result<(), String> {
    let local_port = format!("localport={port}");
    let name = format!("name={FIREWALL_RULE}");
    let (code, _) = run(
        "netsh.exe",
        &[
            "advfirewall",
            "firewall",
            "add",
            "rule",
            &name,
            "dir=in",
            "action=allow",
            "protocol=TCP",
            &local_port,
            "profile=private,domain",
        ],
    );

    match code {
        0 => Ok(()),
        ERROR_ACCESS_DENIED => elevate(&["firewall", &port.to_string()]),
        _ => Err(format!("防火墙规则没加上（netsh 返回 {code}）。")),
    }
}

fn has_firewall_rule(port: u16) -> bool {
    let name = format!("name={FIREWALL_RULE}");
    let (code, text) = run("netsh.exe", &["advfirewall", "firewall", "show", "rule", &name]);
    code == 0 && text.contains(&port.to_string())
}

// ------------------------------ 令牌 ------------------------------

pub fn regenerate_token() -> Result<(), String> {
    let token = format!(
        "{}{}",
        uuid::Uuid::new_v4().simple(),
        uuid::Uuid::new_v4().simple()
    );

    fs::create_dir_all(home()).map_err(|error| format!("写不进令牌文件：{error}"))?;
    fs::write(token_file(), token).map_err(|error| format!("写不进令牌文件：{error}"))?;

    // 中心只在启动时读令牌，改完得让它重新起一次，否则新令牌不生效。
    restart()
}

fn read_token() -> String {
    fs::read_to_string(token_file())
        .map(|text| text.trim().to_string())
        .unwrap_or_default()
}

// ------------------------------ 配置与日志 ------------------------------

fn read_listen() -> String {
    fs::read_to_string(settings_file())
        .ok()
        .and_then(|text| serde_json::from_str::<serde_json::Value>(&text).ok())
        .and_then(|value| value["Listen"].as_str().map(|text| text.to_string()))
        .filter(|text| !text.is_empty())
        .unwrap_or_else(|| DEFAULT_LISTEN.to_string())
}

fn write_listen(listen: &str) -> Result<(), String> {
    let path = settings_file();
    fs::create_dir_all(home()).map_err(|error| format!("写不进配置：{error}"))?;

    // 只动 Listen，别的键（比如 Agents）原样留着。
    let mut settings = fs::read_to_string(&path)
        .ok()
        .and_then(|text| serde_json::from_str::<serde_json::Value>(&text).ok())
        .and_then(|value| value.as_object().cloned())
        .unwrap_or_default();
    settings.insert("Listen".into(), serde_json::Value::String(listen.into()));

    let text = serde_json::to_string_pretty(&settings).map_err(|error| error.to_string())?;
    fs::write(&path, text).map_err(|error| format!("写不进配置：{error}"))
}

fn port_of(listen: &str) -> u16 {
    let authority = listen
        .split("://")
        .nth(1)
        .unwrap_or(listen)
        .split('/')
        .next()
        .unwrap_or(listen);
    authority
        .rsplit(':')
        .next()
        .and_then(|port| port.parse().ok())
        .unwrap_or(8787)
}

fn is_lan(listen: &str) -> bool {
    listen.contains("0.0.0.0") || listen.contains("[::]") || listen.contains('*')
}

/// 这台机器在局域网里的地址：连一个 UDP 目标只为了问路由表自己是谁，不发包。
fn local_address() -> Option<String> {
    let socket = std::net::UdpSocket::bind("0.0.0.0:0").ok()?;
    socket.connect("8.8.8.8:80").ok()?;
    let ip = socket.local_addr().ok()?.ip();
    if ip.is_loopback() || ip.is_unspecified() {
        None
    } else {
        Some(ip.to_string())
    }
}

fn tail_log(lines: usize) -> Vec<String> {
    let mut file = match fs::File::open(log_file()) {
        Ok(file) => file,
        Err(_) => return Vec::new(),
    };

    let length = file.metadata().map(|meta| meta.len()).unwrap_or(0);
    let _ = file.seek(SeekFrom::Start(length.saturating_sub(64 * 1024)));

    let mut buffer = Vec::new();
    let _ = file.read_to_end(&mut buffer);

    let all: Vec<String> = String::from_utf8_lossy(&buffer)
        .lines()
        .filter(|line| !line.trim().is_empty())
        .map(|line| line.trim_end().to_string())
        .collect();

    all[all.len().saturating_sub(lines)..].to_vec()
}

/// 运行时长从日志里最后一条「中心启动」反推：服务跑在系统账户下，普通权限读不到它的进程时间。
fn since_text() -> String {
    let started = tail_log(400)
        .into_iter()
        .rev()
        .find(|line| line.contains("中心启动"))
        .and_then(|line| parse_log_time(&line));

    let Some(started) = started else {
        return String::new();
    };

    // 两边都用同一个时区偏移，拿不到本地时区就一起退回 UTC，差值依然对。
    let now = time::OffsetDateTime::now_utc().to_offset(local_offset());
    let span = now - started;
    if span.whole_hours() >= 1 {
        format!("已运行 {} 小时 {} 分", span.whole_hours(), span.whole_minutes() % 60)
    } else if span.whole_minutes() >= 1 {
        format!("已运行 {} 分钟", span.whole_minutes())
    } else {
        "刚启动".to_string()
    }
}

fn local_offset() -> time::UtcOffset {
    time::UtcOffset::current_local_offset().unwrap_or(time::UtcOffset::UTC)
}

fn parse_log_time(line: &str) -> Option<time::OffsetDateTime> {
    // 日志行首是「2026-11-18 19:22:36.866」，取秒级那一段就够。
    let head = line.get(..19)?;
    let format = time::macros::format_description!("[year]-[month]-[day] [hour]:[minute]:[second]");
    let parsed = time::PrimitiveDateTime::parse(head, &format).ok()?;
    Some(parsed.assume_offset(local_offset()))
}

// ------------------------------ 提权 ------------------------------

/// 需要管理员时把自己再拉起来一次（UAC），子进程做完就退。
fn elevate(args: &[&str]) -> Result<(), String> {
    let exe = std::env::current_exe().map_err(|error| format!("找不到自己的位置：{error}"))?;
    let script = format!(
        "Start-Process -FilePath '{}' -ArgumentList '{ELEVATE_FLAG} {}' -Verb RunAs -Wait",
        exe.display(),
        args.join(" ")
    );

    let status = Command::new("powershell.exe")
        .args(["-NoProfile", "-WindowStyle", "Hidden", "-Command", &script])
        .creation_flags(CREATE_NO_WINDOW)
        .status()
        .map_err(|error| format!("起不了管理员进程：{error}"))?;

    if status.success() {
        Ok(())
    } else {
        Err("没拿到管理员权限，这一步没做成。".into())
    }
}

/// 提权子进程的入口：做一件事就退，不建窗口。
pub fn run_elevated(args: &[String]) -> i32 {
    let first = match args.first() {
        Some(value) => value.as_str(),
        None => return 2,
    };

    match first {
        "start" => {
            run("sc.exe", &["start", SERVICE]);
            wait_for(true).ok();
            0
        }
        "stop" => {
            run("sc.exe", &["stop", SERVICE]);
            wait_for(false).ok();
            0
        }
        "restart" => {
            run("sc.exe", &["stop", SERVICE]);
            wait_for(false).ok();
            run("sc.exe", &["start", SERVICE]);
            wait_for(true).ok();
            0
        }
        "autostart-on" => run("sc.exe", &["config", SERVICE, "start=", "auto"]).0,
        "autostart-off" => run("sc.exe", &["config", SERVICE, "start=", "demand"]).0,
        "firewall" => match args.get(1).and_then(|port| port.parse::<u16>().ok()) {
            Some(port) => match add_firewall_rule(port) {
                Ok(()) => 0,
                Err(_) => 4,
            },
            None => 3,
        },
        _ => 3,
    }
}

// ------------------------------ 给界面用的小动作 ------------------------------

pub fn copy_text(text: &str) -> Result<(), String> {
    let mut child = Command::new("clip.exe")
        .stdin(Stdio::piped())
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .map_err(|error| format!("复制失败：{error}"))?;

    if let Some(mut stdin) = child.stdin.take() {
        // clip 收 UTF-16 也行，但 UTF-8 更省事，它对换行敏感。
        stdin
            .write_all(text.as_bytes())
            .map_err(|error| format!("复制失败：{error}"))?;
    }

    child.wait().map_err(|error| format!("复制失败：{error}"))?;
    Ok(())
}

pub fn open_target(target: &str) -> Result<(), String> {
    match target {
        "url" => {
            let listen = read_listen();
            Command::new("cmd.exe")
                .args(["/c", "start", "", &listen])
                .creation_flags(CREATE_NO_WINDOW)
                .spawn()
                .map_err(|error| format!("打不开地址：{error}"))?;
            Ok(())
        }
        "data" => open_folder(&data_directory()),
        "logs" => open_folder(&log_file().parent().map(PathBuf::from).unwrap_or_else(home)),
        _ => Err("不认识的目标。".into()),
    }
}

fn open_folder(path: &std::path::Path) -> Result<(), String> {
    let _ = fs::create_dir_all(path);
    Command::new("explorer.exe")
        .arg(path)
        .spawn()
        .map_err(|error| format!("打不开目录：{error}"))?;
    Ok(())
}
