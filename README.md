# NandTest

产品名称是领存NFA1000闪存测试仪。仓库名仍是 NandTest。目标系统是 Windows 11 x64。技术栈仍是 .NET 10 和 React。旧软件不作为兼容层；旧功能按合法资料独立重写，并和本期规划放在同一套应用里。

器件 datasheet 和平台管理 ICD 是两层，不能互相填数。没有已核实的 datasheet 包时，料号、命令、地址组织、时序、电压、状态和判据保持关闭。只写来源文字不会变成已核实。2400 MT/s 只属于已核实的 WD 量产约束，不是所有厂商或未核实料号的全局上限，也不代替额定、表征或实际覆盖。三温档只是未核实候选，SKU 规格核实前不授予器件许可，也不允许实机。

管理面目前只有草案只读子集：长度前缀帧、协商和状态查询，在内存替身里测试。默认禁止实机连接。变更、安全许可、恢复和真实结果查询没有资料，保持待定。

参考附件和保密原文不在本仓库。用户授权不等于协议冻结、固件采用或实机通过。

正式页面包含配置和自定义脚本的主机计划。测试夹具里的闭环只用于自动化测试。19 号清单里的其余能力都还在，一期没有完成。旧能力允许以后按合法资料独立重写；本仓库没有读取、导入或反编译旧实现。

本次没有写入 `D:\NandFlashTester`。那个路径只是后续本机交付目标。

最终用户在 Windows 上使用已发布的 `win-x64` 目录：双击 `NandFlashTester.cmd` 启动并打开本机浏览器，用同目录的 `stop.ps1` 停止。不需要手敲 `npm` 或 `dotnet`。开发机上的发布命令是 `dotnet publish src/NandFlashTester.Api/NandFlashTester.Api.csproj -c Release -r win-x64 --self-contained true`。Linux 上生成 PE32+ 只证明构建，不证明 Windows 执行。仓库里的 `windows-x64` 工作流若在 `windows-latest` 跑通测试，才是 Windows 执行证据；在那之前执行验收保持待验证。

## 环境

本次实际验证：

- .NET SDK 10.0.403，共享框架 `Microsoft.NETCore.App` / `Microsoft.AspNetCore.App` 10.0.14，目标框架 `net10.0`。运行中的 `/api/health` 回报 `.NET 10.0.14`
- `Microsoft.Data.Sqlite` 10.0.14（传递依赖 `SQLitePCLRaw.lib.e_sqlite3` 2.1.12）
- 测试包：xUnit 2.9.3，`Microsoft.NET.Test.Sdk` 17.14.1，`Microsoft.AspNetCore.Mvc.Testing` 10.0.3
- Node.js 22.23.4，npm 10.9.9
- 前端：React 19.3.1，Vite 7.3.7，TypeScript 5.9.3，`@vitejs/plugin-react` 5.2.0

`global.json` 要求 10.0 功能带上的最新 SDK。若 `dotnet` 不在 `PATH` 中，先安装 .NET 10 SDK，或把已安装目录加进 `PATH`。

## Windows 用户启动

从本分支 GitHub Actions 的工件 `nfa1000-win-x64` 下载并解压。双击 `NandFlashTester.cmd` 启动，浏览器打开 `http://127.0.0.1:5080/`。双击 `stop.cmd` 停止。版本、完整提交和构建时间在同目录 `build-info.json`，文件哈希在 `SHA256SUMS.txt`，说明在 `START.txt`。不需要安装 .NET 或 Node。这个发布没有在用户电脑上安装或运行，也没有写入 `D:\NandFlashTester`。

## 启动

在仓库根目录：

```bash
dotnet test NandFlashTester.slnx
cd web && npm install && npm run build && cd ..
dotnet run --project src/NandFlashTester.Api
```

浏览器打开 `http://127.0.0.1:5080`。

只改界面时可以分两个进程。先启动 API，再在 `web` 目录执行 `npm run dev`，打开 `http://127.0.0.1:5173`。开发服务器把 `/api` 转到 `127.0.0.1:5080`。

SQLite 默认在 `src/NandFlashTester.Api/data/nand-sim.db`。可以用环境变量换路径：

```bash
DatabasePath=/绝对路径/nand-sim.db dotnet run --project src/NandFlashTester.Api
```

## 这一页怎么用

正式入口是未连接的实机页面。没有设备时显示未连接，执行保持禁用，不会改用测试夹具。

配置：填写名称、种子、块和循环次数，点「写入自定义脚本」。这只生成文本。

自定义脚本示例：

```
name 页编程比较
seed 1
var block = 0
var pages = 2
erase block
loop pages
  program block i
  read block i
  compare block i
end
```

`var` 是整数变量。`loop` 次数必须在 1 到 32，嵌套最多 2 层，`i` 是本层从 0 开始的序号。`if 左值 == 右值` 在计划时选择分支，用 `else` 和 `end` 收束。校验失败会给出行和列。点「新建」「保存」「打开」留下脚本；「展开主机计划」给出擦除、写入、读回、比较的计数和逐步日志，错误计数写「未统计」；「停止」记在发送之前；「导出」下载同一份计数和日志。这些记录都标明硬件未执行。没有已核实的 SKU 规格时，实机请求会被拒绝。自动化测试可以用隔离介质核对控制流和错误位数，该结果标为自动化测试，不会出现在正式页面，也不能当成实测成功。

同一页也接受用户授权复用的命令文本，例如 `BKERAS`、`PGPROG`、`PGRDCP`、`REPEAT` 和 `VSET08`。压缩包里的 class 没有执行，也没有放进仓库。`CMND`、`SNDAD`、`SETFTR`、`STATUS`、`BUSYTM`、`ECCANA` 和文件路径只被识别，不发送、不读路径。主机计划仍然不是已完成的擦写读。

自动化测试夹具仍是单独入口，只保留自己的模拟记录，不作为正式页面。

历史列表来自 SQLite。重启服务后，已结束任务仍可查询；当时还在跑的任务标为「已中断」，不会接着写。CSV 列与页面上的任务字段、步骤和错误位置一致。

固定种子可以复现。`increment` 的第 `i` 个字节是 `(seed + ((block * 65536 + page) * pageSize + i)) mod 256`。`prng` 为按页重置的 xorshift32。`constant` 整页填同一个字节。

## 本版已落地的子集

| 子项 | 本版 |
| --- | --- |
| F01 基础闭环 | 模拟擦除、图案写入、读回比较、进度、取消、SQLite、CSV |
| F03 图案 | increment、constant、prng |
| F07 配置与自定义脚本 | 同一页的配置和自定义脚本：行列校验、变量、有界循环、分支和主机计划 |
| F11 设备信息 | 合成标识、状态、参数页 |
| F12 基础 UI | 中文测试页、进度、取消、历史、CSV |
| F28 基础结果 | 配置快照、种子、逐步结果，CSV 与 SQLite 一致 |

以上都是子集，不是对应 F 编号的完整验收。

## 仍未实现、但保留的范围

一期 P0 后续增量：F02 命令抽象，F04 忙时间，F05 ECC/UBER，F06 规模和速率，F07 完整专业脚本，F08 P/E 长测恢复，F09 写干扰，F10 读干扰，F12 完整双模式，F13 错误分布，F14 电压拉偏，F15 时序校准，F16 三温，F17 公开命令最小集，F19 Retry，F20 五级分档和 MES，F21 QLC，F25 Handler，F26 集群，F27 权限审计，F28 完整报告，F29 全自动产线。

现行 P1：F07 单步调试，F17 完整 Vth，F18 保持向导，F28 对比交互。

现行 P2：F22 认证，F23 AI，F24 FTL 教学模拟器。F24 与本设备模拟器不是同一功能。

完整对照也可以看运行中的 `GET /api/meta`。其中 `jsonEntryIsSslCompatible` 为 false。

## 明确不做

- 不把模拟计数写成实测 MT/s、电压精度、温度或量产结论。
- 不实现真实电压、温箱或 Handler。不导入旧脚本语法。
- 不读取反编译、旧程序或专有 datasheet。
- 非法脚本不会被当成可以下发的内容，也不会在没有硬件时被记成已执行。
