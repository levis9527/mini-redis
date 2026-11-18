# mini-redis

Mini Redis 是一个 Windows x64 绿色桌面程序，用来连接 Redis 并查看、编辑键。它不是 Redis 官方产品。界面是 WinForms，不带浏览器，也不带 WebView2。

## 运行

发布物是一个目录打成的 ZIP，不是会把运行时解压到临时目录的单文件 exe。解压到当前用户可写的文件夹后，运行其中的 `MiniRedis.exe`。目录里的 `coreclr.dll`、`hostfxr.dll` 等文件要留在 exe 旁边。不需要安装程序，也不需要另外安装 .NET。

配置在程序目录的 `data/connections.json`。操作记录在程序目录的 `logs/audit.jsonl`。两者都相对可执行文件目录，不看你从哪个工作目录启动。目录不能写时，程序会说明路径并退出，不会改存到 AppData。

整个文件夹可以拷走。密码默认只留在本次运行的内存里。如果勾选保存，使用 Windows DPAPI 的当前用户范围加密；换一台电脑或换一个 Windows 用户后解不开。不要把“目录能搬走”理解成“保存的密码也能用”。

程序未签名。Windows 可能提示未知发布者。

## 支持的系统

目标框架是 .NET 10（LTS）。微软支持策略页（标注更新于 2026-11-10）写明 .NET 10 于 2025-11-11 发布，处于 Active 支持，支持结束日是 2028-11-14。不使用 .NET 11。

本仓库用 SDK 10.0.404 编译。桌面项目的目标框架是 `net10.0-windows10.0.17763.0`，只发布 win-x64。17763 是 Windows 10 版本 1809。按 Microsoft Learn「Install .NET on Windows」里 .NET 10 的表格，运行时还列出 Windows 11（26H1、25H2、24H2、23H2 Ent/Edu）x64、Windows 10 21H2，以及 1809 / 1607 的 LTSC 与 Enterprise。本程序没有把目标平台降到 1607，因此不把 1607 写成这个程序的最低版本。不发布 x86 或 Arm64，也不支持 Windows 7 / 8.1。Windows Server 2012 / 2012 R2 按该页仍可能需要 Visual C++ 2015–2019 可再发行组件。服务器列表还包括该页上的 Windows Server x64。

这里的编译和自动化测试是在 Linux 上做的，不能代替在上述 Windows 上打开窗口。没有在 Windows 上实测过界面，也不填写启动时间或工作集。

在这台 Linux 上交叉发布的 win-x64 自包含目录有 280 个文件，目录合计 150010430 字节，打成 ZIP 后 57102299 字节。目录里有 `MiniRedis.exe`、`coreclr.dll` 和 `hostfxr.dll`。这不是单文件包，也不是 Windows 上的运行指标。

## 怎么用

默认只读，每次启动都是只读。连接成功后不会自动扫描。

键列表默认是 SCAN。`开始查询` 发第一批，`加载更多` 每次只再发一条 SCAN。`COUNT` 不是返回条数。空批且游标不是 0 不算结束。游标按无符号整数原样保存。类型过滤加在同一条 SCAN 的 `TYPE` 上；服务器不支持就报错，不会为了凑类型连续扫描。

`加载全部` 单独确认一次，然后连续 SCAN，直到游标为 0 或你停止。没有虚构的总数。到了界面上限会停下，并写明还没有全部加载。

精确查询里，完整键名只做 `EXISTS` / `TYPE` / `PTTL`，键名中的 `*`、`?`、`[]` 都是字面值。只有输入 `KEYS` 加一个模式才会执行 `KEYS`。`KEYS` 即使没有 `*` 也要确认：它是一次阻塞的全库匹配，界面分页不能减轻服务器上的成本。

选中键之后才读值。超过 256 KiB 的字符串只预览并禁止保存。非 UTF-8 只读。Hash、Set、ZSet 按游标翻页，List 按 `LRANGE` 翻页。保存不会用当前页盖掉整个集合。

命令面板发送 Redis 命令，不是操作系统 shell。传输库把 `KEYS` 算作管理命令，所以连接时打开了它的 AllowAdmin，否则确认后的 `KEYS` 也发不出去。这不是安全开关。`FLUSHDB`、`FLUSHALL`、`SHUTDOWN` 仍在发送前直接拒绝，确认也不能放行。只读模式只放行 `COMMAND INFO` 标成 `readonly` 且没有 `write` 的命令；拿不到元数据就拒绝。可写模式仍会转发未知命令。`EVAL`、`FCALL`、`MULTI` 和模块命令可能间接做被拒绝的事，客户端的命令名检查管不到。需要限制时，请在 Redis ACL 里去掉对应用户的 `flushall`、`flushdb`、`shutdown`、`@dangerous` 以及不需要的 `@scripting`。每一行命令也不保证能组成可靠事务。

操作记录只保存上海时间、连接和库、脱敏后的命令、成败。不保存返回值。密码、脚本、未知参数和写入值会从回显、历史和日志里拿掉。服务器错误如果可能带回这些内容，也会隐藏。

## 开发

```bash
dotnet test MiniRedis.sln -c Release
dotnet publish src/MiniRedis.App/MiniRedis.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=false -o artifacts/win-x64/MiniRedis
```

集成测试会自己拉起一个只监听 127.0.0.1 的临时 `redis-server`，不会连接你正在使用的实例，也不会发送 `FLUSHDB`、`FLUSHALL` 或 `SHUTDOWN`。

设计约定和验收项在 `docs/implementation-plan.md`。

## CI 产物

GitHub Actions 工作流 `windows` 会在推送、pull request 和手动运行时，于 `windows-latest` 上还原、编译、测试，并上传自包含目录 ZIP，保留 14 天。这不是 GitHub Release，也没有签名。

下载：打开该次运行的 Actions 页面，在 Artifacts 里取 `MiniRedis-win-x64-portable`。如果某次运行没有在 Windows 上测出启动时间或内存，以产物里的 `smoke-metrics.txt` 原文为准，不要把缺失的数字补上。
