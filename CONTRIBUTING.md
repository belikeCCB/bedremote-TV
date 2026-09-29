# 贡献说明

## 怎么编译、怎么跑

```
build.cmd            用系统自带的 csc.exe 编译出 bedremote.exe（不需要装任何东西，不需要管理员）
build.ps1 -Out x.exe 同上，可以编出第二份用于测试（界面也能跑在别的端口：`x.exe --gui --port=8791`）
bedremote.exe        不带参数 = 图形界面（一个窗口 + 托盘，服务在后台线程里跑）
run.cmd              还是前台跑；想要老的控制台行为用 `bedremote.exe --console`
run-hidden.vbs       后台静默跑（界面版更省事：`bedremote.exe --tray` 就是启动即缩进托盘）
stop.cmd             停服务
pair.cmd             没起就悄悄起 + 在默认浏览器打开配对二维码页
autostart.cmd        写当前用户的 Run 键开机自启（unautostart.cmd 取消）；
                     界面里那个「开机自动启动」复选框做的是同一件事，用的是 --tray
allow-firewall.cmd   一条 UAC 把入站端口放行（自写程序不能靠代码加规则）
tools\monprobe.cs    只读侦察显示器与 DDC/CI 能力（开发用，不参与主构建）
tools\shot.ps1       把界面窗口截成 PNG（PrintWindow，不抢前台）—— 改布局的验收手段
tools\uiclick.ps1    在沙箱副本里真的点界面按钮、读日志框文本 —— 回归"按钮到底通没通"
```

编译器是 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，即 .NET Framework 4.x。
**只支持到 C# 5 语法**：没有字符串内插、没有 `?.`、没有表达式体成员。这不是手忘了，是编译器的硬限制。
`build.ps1` 里已经钉了 `-langversion:5`，用错了语法会在编译期直接报错（以前只是碰巧没人写错）。
`build.ps1` 带 `-codepage:65001`：`.cs` 是 UTF-8 无 BOM 存的，不加这个参数在非 936 代码页的机器上
会把中文字面量编成乱码（界面上的按钮文字直接变问号）。

改 `.cs` 要重新编译；改 `www\*.html` 不用（页面是运行时从磁盘读的）。

## 七个必须知道的坑

1. **`*.cmd` / `*.vbs` 必须是 GBK + CRLF**，不要转成 UTF-8，也不要让编辑器改成 LF。
   中文批处理在 UTF-8 下，GBK 会把行尾的 `0x0A` 当成双字节汉字的尾字符**吃掉**，
   下一条命令被粘进上一行的注释里 → cmd 报"不是内部或外部命令"，而且窗口一闪就没，只留一句"异常"。
   改完过一遍：`iconv -f UTF-8 -t GBK x.cmd | unix2dos`，再验文件里 **CR 数 == LF 数**。
   `.gitattributes` 已经把这些文件标成 `-text`，别让 git 改动它们。
1b. **`*.ps1` 反过来：保持纯 ASCII，连注释也是。** 同一个"吃换行"的机制，在 PowerShell 里的表现是
   **整行被并进上一行的注释** —— 脚本少跑了几行，变量莫名其妙是空值，报错还指在别的行上
   （实测：注释里的中文让下一行的 `$report = Join-Path ...` 一起消失）。
   要写中文按钮名就用码点拼：`[char][Convert]::ToInt32('65AD',16)`；
   实在要中文文本，就存成 UTF-8 with BOM（`printf '\xEF\xBB\xBF' | cat - x.ps1 > y.ps1 && mv y.ps1 x.ps1`）。
   `.cs` 不受这条约束，因为 build.ps1 显式指定了 `-codepage:65001`。
   **这条现在由 CI 和 `package.ps1` 强制**（`.ps1` 里出现非 ASCII 就红）—— 因为作者本人在 0.11.3
   又踩了一次：给 `tools/phoneshot.ps1` 加了两行中文注释，脚本当场解析失败。别指望"我记得"。
2. **不要用 `HttpListener`**：非管理员时它要 URL ACL，绑不上。所以 HTTP 层是手写的 `TcpListener`。
   代价是没有现成的 multipart —— 表单/查询按 `application/x-www-form-urlencoded` 自己解析。
3. **控制台不要设 UTF-8**。这台机（以及很多中文 Windows）代码页是 936，
   `Console.OutputEncoding = UTF8` 反而满屏乱码；让 .NET 按控制台代码页自己转。
   要 UTF-8 输出就在子进程里带 BOM 写文件，别动控制台。
4. **鼠标键盘注入的四个坑**（`src/BedRemote.cs` 里都有注释）：
   - 相对位移会被"提高指针精确度"放大且非线性 → 读当前坐标 + 算落点 + 发 `MOVE|ABSOLUTE|VIRTUALDESK`。
   - `MOUSEEVENTF_ABSOLUTE` 单独发是**静默无效**，必须与 `MOUSEEVENTF_MOVE` 同用。
   - 组合键里的单个字母必须走虚拟键码 `0x41–0x5A`，走 `KEYEVENTF_UNICODE` 修饰键会被完全忽略
     （Ctrl+A 变成打出字母 a）。
   - "读坐标 → 算 → 写"必须原子，否则并发 move 丢步（注入段有全局锁）。
5. **DPI：manifest 里只写 `dpiAware=true`（系统级感知），别写 PerMonitorV2。**
   - 完全不管 DPI 不行：`GetSystemMetrics` / 显示器矩形会拿到虚拟化尺寸（实测 2560x1440 报成 2048x1152），
     `SendInput` 的坐标全错。
   - 但 .NET Framework 的 WinForms 配 PerMonitorV2 会踩另一个坑：窗口跨屏时被按新 DPI 重缩放，
     控件不跟着重排（实测 800 宽的窗体在 125% 主屏和 100% 副屏之间变成 640，右边整列被裁掉）。
     系统级感知两头都不占：坐标是物理像素，缩放只在进程启动时算一次。
6. **锁屏 / 安全桌面上注入会被静默丢掉**。判据是 `LogonUI` / `Consent` 进程在不在（近似判据）。
   `OpenInputDesktop` + `GetUserObjectInformation` 在这台机上读不回桌面名，别再试这条路。
7. **"按住"这件事必须自己兜底**。`keybd_event`/`SendInput` 的 down 不发 up 就一直算按着 ——
   手机上"抬手"那个事件最容易丢（锁屏、切后台、WiFi 抖、页面被系统回收），丢一次的后果不是少一个事件，
   是游戏里角色一直往前跑。所以：客户端每帧上报"我现在按住的全集"让服务端**差分**（丢帧自愈），
   服务端按 IP 记心跳、超时全松，客户端切页签/关页面用 `sendBeacon` 补发一次 `c=release`。
   别指望"down/up 各发一次"能可靠配对。
8. **同一件事只许有一处夹边界**。摇杆位置存的是 0..1 比例，画的时候要换算成像素 ——
   模型里夹一次（还是随手写的 `0.98`）、落笔时按"装得下"再夹一次，两份算法一旦不一致，
   就会出现"画面钉在角落、模型还在往外涨"，往回拖半天不动。规则：只在 `gpPlace` 前夹**模型**，
   画的时候不再另外夹；`gpBox` 是尺寸的唯一算法。测试盯的是"存的位置 == 画出来的像素"。
9. **别让 CSS 和脚本抢同一个属性**。旋钮居中原来靠 `.knob` 的负 margin，推摇杆时脚本往同一个
   margin 写像素偏移，松手写回 `0px` → 居中跟着没了，旋钮赖在右下角。居中交给 `transform`，
   margin 只当偏移用。同理：任何"看起来是样式"的东西，只要脚本会写它，就不能再靠样式做默认值。
10. **改了默认值要管老存档**。布局存在手机 `localStorage` 和电脑 `bedremote.json` 两份里，
    只改代码，用户打开看到的还是坏样子。所以带版本号（`ver`）做一次性重排，
    而且只重排"出厂那几个 id"的位置尺寸，用户改过的名字/键位/透明度和自己加的控件一律不动。

## 改手机页 JS 怎么测（不用真手机）

`www/phone.html` 是一个 IIFE，全局拿不到，所以 `tools/gpharness.js` 把「手柄」那段代码**按注释标记切出来**
丢进 `vm` 里跑，配一个只实现到够用程度的假 DOM（`clientWidth/Height`、`dataset`、`classList`、`closest`、
`querySelectorAll`、`appendChild`），然后用合成的 `pointerdown/move/up` 驱动它，断言"发出去的请求字符串"。
好处：圆盘→按键映射、死区、疾跑阈值、按住/点按、视角累加、编辑模式拖拽、保存/读回、切页松手，
全都能在几秒内跑完，而且**不会往系统里注入任何东西**。
两个已知前提：切出的代码块必须自包含（只依赖 `$`/`el`/`send`/`buzz`/`T` 这些桩），
以及假 DOM 的 `movementX` 是 0 —— 这恰好暴露了"用 movementX 判断有没有拖动"的真 bug，所以别在测试里给它补上。

服务端"注入到底有没有生效"用 `tools/keystateprobe.cs`：它只轮询 `GetAsyncKeyState`（全局键态，
SendInput 会更新它）并把 DOWN/UP 写进日志，不动任何窗口。
**测的时候用 shift/ctrl 这类修饰键，别用字母** —— 按住不产生字符，但"按住再松开"会往别人正在输入
的窗口里打一个字母；鼠标键更别测，那是一次真点击。带正/负对照：先静置一秒确认探针不乱报，再发帧。

配置读没读通用 `tools/cfgprobe.cs`：它把一份 `bedremote.json` 交给**真的 `Config.Load`** 读一遍，
打印读到了什么，并逐个核对面板按钮的 `act` 在不在服务端动作表里（`Program.IsInputCmd`，所以它要连 src 一起编译、
用 `-main:CfgProbe` 选入口）。**为什么需要它**：配置解析失败时服务端是"退回内置默认"，
界面上一切正常，只是用户的改动没生效 —— 这种失败从手机页面完全看不出来。
CI 里那步"示例配置必须还能读懂"跑的就是它，负对照办法：把 example 里某个 `act` 改成不存在的动作，它必须报红。

`tools/gpfitcheck.js` 管的是另一头：**布局摆得开、拖得回**。它用同一个假 DOM 跑真的 `gpPlace`，
读的是写进 `style` 的像素而不是模型里的比例，检查十种画布比例（竖屏/横屏/平板/电视/超宽/方屏/小窗）下
不重叠不出界、"存的位置 == 画出来的位置"、拖出角再拖回来画面第一下就动、旋钮松手回圆心、
老存档重排不许冲掉用户改的东西。改任何布局相关代码（`gpRects`/`gpBox`/`gpFit`/`gpPlace`/`GP_DEF`）都要跑它。
**没有 node 也能跑**：`node-repl` 里 `createRequire` 一下，或用 ZCode 自带的无头 node。
它的第一个参数可以是另一份 `phone.html` —— 拿 `git show HEAD~1:www/phone.html` 当**负对照**，
看到它一片红，才知道这些检查真的会红（全绿的第一个测试基本都是假的）。

**界面截图怎么来**（README 顶上那六张）：这台机的 Chrome/Edge **无头模式渲染直接崩**
（`Abnormal renderer termination`，跟它坏掉的 PDH 计数器是同一类问题），内置浏览器面板没有可见表面时也截不了，
所以 `tools/phoneshot.ps1` 走"真浏览器窗口 + PrintWindow"：`--app` 起一个 390x844 的窗口、
`--user-data-dir` 用一次性目录、按"命令行里带这个目录"去找真正 owning 窗口的 PID（**你 Start-Process 的那个进程往往不是窗口的主人**）。
截图前先想清楚里面有没有本机信息：地址、电脑名、前台窗口标题都会进图 ——
所以截图是从**沙箱实例**（另目录、另端口、`bedremote.example.json` 改的演示名）上拍的，
再用 `tools/redact.ps1` 把剩下的地址/机器名糊成实心块（故意不模糊：模糊是能被读回来的）。

## 加一个动作要动几个地方

以"新增一个指令 `c=foo`"为例：

1. `src/BedRemote.cs` 的 `Dispatch` switch 里加 case；`ack` 是要回给手机/脚本的字符串。
   如果是键盘鼠标类动作，把名字加进 `IsInputCmd`，这样锁屏时会被 `denyWhenLocked` 挡下并提示。
2. 想让它在面板按钮里可配，就同步三处：`www/phone.html` 的 `fire()`（动作怎么变成 HTTP 请求）、
   `www/edit.html`（编辑器里能选到它）、README 的 `act` 表和 `src/Config.cs` 的默认面板（如果要进默认配置）。
3. 手机端能自己完成的行为（切页签、清修饰键）不要走网络。

## 改显示器相关代码前必读（`src/Display.cs`）

- **屏的身份用设备路径里的 `UID`，不是 `targetInfo.id`**：同一块屏在扩展和克隆两种拓扑下 target id 会变
  （实测电视 268 被克隆后变成 50331916）。
- **唤醒时不要挑"已经被别的活跃屏占用的 source"**：那会变成克隆（两块屏同一画面、帧率被拉齐）。
  顺序：数据库里存的布局 → 空闲 source 的候选 → 整组扩展。
- **每次动完必须回读校验，不满足预期就回滚**。`SDC_VALIDATE` 只是"参数合法"，不是"结果是我要的"
  —— 我就是靠回读才发现自己把主屏和电视搞成克隆的。
- DDC/CI 的读有约 10% 随机丢包，单次失败别急着判"不支持"。物理屏句柄必须 `DestroyPhysicalMonitors` 还回去。
- 想看某台机器到底支持哪些玩法：编译并跑 `tools\monprobe.cs`，它只读、不改任何显示状态。

## 验证方式（别只信返回值）

- 指令层：`curl "http://127.0.0.1:8765/cmd?c=..."` 直接打，看 `ack`。
  **测中文参数要用 UTF-8 百分号转义**：Git Bash 里的 curl 会按本机代码页把中文编成 GBK，
  服务端按 UTF-8 解出来是乱码，看着像产品 bug，其实是测试工具在骗你。
- 显示器状态：`GET /cmd?c=mons` 回读 `act/prim/clone/坐标`，别看接口返回 true 就当成功。
- 二维码：把页面上**真实渲染出来的** SVG 走 `<img>` → canvas → `getImageData` → 独立解码库（jsQR）读回来比对。
  只验证"我生成的矩阵"是不够的，渲染路径（静区、缩放、边缘糊掉）才是扫码失败的重灾区。
- 注入是否真生效：记事本 / 浏览器输入框里读回字符；剪贴板类操作要验证"用完还原原内容"。
- **多机功能不需要两台真机器**：同一台机器上起两个实例（`--port=` 分开、各自一个目录和一份 json）就能验完
  广播、心跳、改名传播、`c=scan`、`c=push` —— Windows 上 UDP 广播会回到本机，两个进程各自 `SO_REUSEADDR`
  绑同一个发现端口也都能收到。它验不到的只有一件事：**广播到底能不能穿过真的交换机**，那必须两台机。
  推送类测试记得用 ASCII 标记词（中文在 .ps1 里会被代码页吃掉，见上面第 1b 条）。
- **关测试实例必须 `-Force`**。带界面的 bedremote 把"关闭窗口"做成了缩到托盘，所以 `taskkill`（不带 `/F`，
  等于发一个关闭请求）会被它当成"用户点了 X"——**进程没死，只是藏起来了**：你以为清干净了，
  其实端口还占着，下一个实例会报"端口被占"。`stop.cmd` 用的就是 `Stop-Process -Force`，照它做；
  杀的时候用精确镜像名或 PID，别拿前缀匹配（曾经差点顺手杀掉一个名字以 b 开头的无关进程）。
- **从标签页做一次"干净检出能不能编"**：`git clone` 到临时目录 → `git checkout <tag>` → 跑 `build.ps1`。
  这一步专门抓"新文件忘了 `git add`"——少一个 .cs 就是编译失败，而在自己工作区里永远发现不了。

## 提 PR 之前

- 跑一遍 `build.cmd`，再手动过 `README` 的快速开始（换一台干净的机器最好）。
- 动到"可能回不来"的硬件状态（显示器拓扑/电源、驱动、存储）时，**先准备好纯软件的退路**
  （不能依赖"人起身去按物理按钮"），并把危险路径默认关闭、显式打开。这条不是客套：
  0.4.0 的 DDC 熄灭就因为在某块面板上是单程票而被默认禁用（见 CHANGELOG 和 SECURITY）。
