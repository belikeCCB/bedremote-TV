# Changelog

遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 风格，版本号按 SemVer。
每个版本都是在同一台 Win10 机器上真实跑过之后才写的，"未验证"的东西一律写在条目里而不是悄悄漏掉。

## [0.6.0] - 2026-09-27

### Added
- **设备台账**：服务端按 IP 记录是谁在控制这台电脑 —— 指令数、最后一条动作、多久之前、页面还开着没。
  新设备第一次接入会在控制台打一行 `[新设备] x.x.x.x 开始控制这台电脑…`，并通过 SSE 推 `{"e":"peers"}`。
  `GET /addr` 新增 `devs` / `peers[]`，`GET /status` 新增 `devs`。
  本机自己的浏览器不算外部设备（认 loopback 和自己网卡上的 IP）。
- **配对页常态显示台账**：`/pair` 上加了「这台电脑现在被几台设备控制」的大数字 + 明细表。
- **手机自助出码**：手机页顶部「加设备」画出当前页面地址的二维码；设了令牌时码里自带令牌，
  别人扫它就直接进来，不用碰电脑、不用输地址。

## [0.5.0] - 2026-09-27

### Added
- **「只留这块屏」** `c=only&mt=<uid>`：一键 = 它当主屏 + 其他屏全部断信号；目标屏处于无信号状态时也能按（先唤醒）。
- **「设为主屏」** `c=primary&mt=<uid>`：只把桌面坐标原点交给这块屏，一块屏都不关。
- **「撤销上一步」** `c=undo`：把动之前存下的显示配置原样放回去（快照只活在进程里）。
- **不点名的轮换** `c=cyclep` / `c=cycleo`：环按 uid 排，"换下一块"。两块屏时是花架子，三块以上才体现价值。
- **切音频输出**：不加代码，用 `run` 白名单一条 `start ms-settings:sound`，手机一个「声音」按钮。

### Changed
- `run` 被拒绝时不再只回一个 `denied`：现在回显"收到的命令名 + 白名单已加载几条 + 配置有没有解析失败"。
- 配置健康可见：`/status` 新增 `cfg` / `cfgErr` / `runCount`，启动横幅打印配置文件路径与白名单条数。
  （以前 `Config.Error` 写了但没人读，配置解析失败是**静默**退回内置默认的。）
- 修掉一处骗人的提示文案：切主屏成功时报的坐标是算错的游标尾值。

### Removed
- 一次性写好的 `IPolicyConfig`（未公开 COM）音频切换实现 —— 没进构建就被删。
  Windows 没有公开 API 能设默认音频输出，为了省两下点击引未公开接口不值当，而且它一炸炸的是常驻服务。

## [0.4.0] - 2026-09-26

### Added
- **按屏点名控制显示器**（`src/Display.cs`）：`c=mons` / `c=blank&mt=<uid>[&sec=N]` / `c=wake&mt=<uid>` /
  `c=rescue&mt=<uid>` / `c=ddcdbg&mt=<uid>`，两套机制：拓扑法（`SetDisplayConfig`，一定能接回来）
  和 DDC/CI（`VCP 0xD6`，不动桌面布局，但不保证能叫醒）。
- 手机页新增「屏幕」页签：每块物理屏一张卡（型号名、分辨率刷新率接口、主屏标记、当前有无信号）。
- 「自动接回：开 (20 秒)」开关、「重插线（救）」按钮、克隆态检测与「修克隆」。
- `tools/monprobe.cs`：只读侦察工具，列出所有显示 path（含插着但没驱动的）和每块屏的 DDC 能力串 / VCP 值。

### Fixed
- 屏的身份改用设备路径里的 `UID`：同一块屏在"扩展"和"克隆"下 `targetInfo.id` 会变（实测 268 → 50331916）。
- 唤醒时不再挑"已被别的活跃屏占用的 source"（那会变成克隆）；顺序改成
  数据库里的布局 → 空闲 source → 整组扩展，且**每次动完回读校验、不对就回滚**。
- `GetPhysicalMonitorsFromHMONITOR` 拿到的物理屏句柄补上 `DestroyPhysicalMonitors`（常驻进程里会漏）。
- DDC 读有约 10% 随机丢包：单次失败不再判"不支持"，重试 3 次；探测结果读通一次就记住，按钮不再忽隐忽现。

### Security
- **DDC 熄灭默认禁用**（`ddcOff` 默认 `false`）。原因：本机主屏接受 `0xD6=04` 之后连 DDC 通道一起断，
  `0xD6=01` 叫不醒而返回值仍是成功，只能起身拔数据线。打开后也一律带 20 秒自动兜底，
  叫醒之后回读面板活性、不应答就自动走"重插线"。

## [0.3.0] - 2026-09-26

### Added
- **配对二维码** `/pair`（`www/pair.html`）+ 一键打开的 `pair.cmd`：手机相机扫一下就直连，不用在手机上敲地址。
  每个网卡地址一张码；每 4 秒自己刷新（DHCP 换 IP 会自动跟着变）；按 `F` 全屏只留码（可以开在电视上躺着扫）；
  实时显示已连接页面数。
- `GET /addr` 接口；`GET /vendor/*` 静态口（只放行 `www/vendor` 下的 js/css，挡 `..`）。
- 二维码内容用 vendored 的 `qrcode.js` 在本地生成，不联网。渲染出的码用独立解码器（jsQR）反向读回验证过。

### Fixed
- 双击 `run.cmd` 第二次不再抛未处理的 `SocketException`（窗口一闪就没、只剩一句"异常"）：
  现在打印"很可能已经在运行" + 重打手机地址 + 指向 `stop.cmd` / `pair.cmd`，返回码 3，窗口停住不消失。
- 仓库里所有 `*.cmd` / `*.vbs` 转成 **GBK + CRLF**。原先是 UTF-8 + LF：
  GBK 会把行尾 `0x0A` 当成双字节汉字的尾字符吃掉，下一条命令被粘进上一行注释里，
  cmd 报"不是内部或外部命令"，而且窗口关得比眼睛快。

## [0.2.0] - 2026-09-26

### Added
- 按钮全部外置到 `bedremote.json` 的 `panels` 段，手机页读配置渲染；内置只剩 触控板 / 打字 / Agent 三页。
- `/edit` 可视化面板编辑器：增删页签/分组/按钮，每行可「试」触发，保存后经 SSE 让手机上已打开的页面**自动重渲染**。
- `run` 动作只认配置里的命令名白名单，手机传不过去任意命令行。
- 锁屏守卫：`LogonUI` / `Consent` 进程在不在 → 锁屏或安全桌面时丢掉注入并往手机上提示。
- `README.md`、`LICENSE`(MIT)、`.gitignore`，以及 `run.cmd` / `run-hidden.vbs` / `stop.cmd` /
  `autostart.cmd` / `unautostart.cmd` / `allow-firewall.cmd`。

## [0.1.0] - 2026-09-26

### Added
- 第一版：`bedremote.exe`（C#，系统自带 `csc.exe` 编译，零安装零管理员）——
  手写 HTTP/1.1 + SSE（`HttpListener` 要 URL ACL，所以直接用 `TcpListener`）+ `SendInput` 注入。
- 指令：`move / abs / btn / wheel / key / combo / text / paste / screen / power / awake / notify / reply / run / mon / reset`。
- 手机页：触控板（相对位移 + 独立点击键，双指滚动，点哪跳哪）、中文直投（`KEYEVENTF_UNICODE`，绕开电脑输入法）、
  剪贴板粘贴并还原原内容、Agent 消息与"允许 / 拒绝"回路。
- 注入侧修掉的四个非显然坑：鼠标加速曲线导致相对位移非线性、`MOUSEEVENTF_ABSOLUTE` 必须与 `MOVE` 同用、
  组合键里的单字母必须走虚拟键码（走 Unicode 会丢掉修饰键）、并发 move 丢步（注入段加全局锁）。
