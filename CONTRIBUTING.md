# 贡献说明

## 怎么编译、怎么跑

```
build.cmd            用系统自带的 csc.exe 编译出 bedremote.exe（不需要装任何东西，不需要管理员）
run.cmd              前台跑，窗口里打印手机要访问的地址
run-hidden.vbs       后台静默跑（没有托盘图标，用 stop.cmd 关）
stop.cmd             停服务
pair.cmd             没起就悄悄起 + 在默认浏览器打开配对二维码页
autostart.cmd        写当前用户的 Run 键开机自启（unautostart.cmd 取消）
allow-firewall.cmd   一条 UAC 把入站端口放行（自写程序不能靠代码加规则）
tools\monprobe.cs    只读侦察显示器与 DDC/CI 能力（开发用，不参与主构建）
```

编译器是 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，即 .NET Framework 4.x。
**只支持到 C# 5 语法**：没有字符串内插、没有 `?.`、没有表达式体成员。这不是手忘了，是编译器的硬限制。

改 `.cs` 要重新编译；改 `www\*.html` 不用（页面是运行时从磁盘读的）。

## 六个必须知道的坑

1. **`*.cmd` / `*.vbs` 必须是 GBK + CRLF**，不要转成 UTF-8，也不要让编辑器改成 LF。
   中文批处理在 UTF-8 下，GBK 会把行尾的 `0x0A` 当成双字节汉字的尾字符**吃掉**，
   下一条命令被粘进上一行的注释里 → cmd 报"不是内部或外部命令"，而且窗口一闪就没，只留一句"异常"。
   改完过一遍：`iconv -f UTF-8 -t GBK x.cmd | unix2dos`，再验文件里 **CR 数 == LF 数**。
   `.gitattributes` 已经把这些文件标成 `-text`，别让 git 改动它们。
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
5. **DPI**：进程是 manifest 声明的 PerMonitorV2（`src/app.manifest`）。少了它，
   `GetSystemMetrics` / 显示器矩形拿到的是虚拟化尺寸（实测 2560x1440 报成 2048x1152），坐标全错。
6. **锁屏 / 安全桌面上注入会被静默丢掉**。判据是 `LogonUI` / `Consent` 进程在不在（近似判据）。
   `OpenInputDesktop` + `GetUserObjectInformation` 在这台机上读不回桌面名，别再试这条路。

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

## 提 PR 之前

- 跑一遍 `build.cmd`，再手动过 `README` 的快速开始（换一台干净的机器最好）。
- 动到"可能回不来"的硬件状态（显示器拓扑/电源、驱动、存储）时，**先准备好纯软件的退路**
  （不能依赖"人起身去按物理按钮"），并把危险路径默认关闭、显式打开。这条不是客套：
  0.4.0 的 DDC 熄灭就因为在某块面板上是单程票而被默认禁用（见 CHANGELOG 和 SECURITY）。
