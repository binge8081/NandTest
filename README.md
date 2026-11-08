# NandTest

新平台 NAND 测试上位机。目标系统是 Windows 11 x64。技术栈仍是 .NET 10 和 React。旧软件不作为兼容层；旧功能按合法资料独立重写，并和本期规划放在同一套应用里。

器件 datasheet 和平台管理 ICD 是两层，不能互相填数。没有已核实的 datasheet 包时，料号、命令、地址组织、时序、电压、状态和判据保持关闭。只写来源文字不会变成已核实。WD 量产验收上限 2400 MT/s 是平台规则，不代替器件额定或表征速率，也不写成实际覆盖。

管理面目前只有草案只读子集：长度前缀帧、协商和状态查询，在内存替身里测试。默认禁止实机连接。变更、安全许可、恢复和真实结果查询没有资料，保持待定。

参考附件和保密原文不在本仓库。用户授权不等于协议冻结、固件采用或实机通过。

本版只交付这个闭环。19 号清单里的其余能力都还在，没有因为首版或界面差异被删掉。旧能力允许以后按合法资料独立重写；本仓库没有读取、导入或反编译旧实现。这里的 JSON 不是 SSL 兼容。

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

简单操作：选「正常成功」或「注入一位失败」，改通道、块、页、图案和种子，点「运行模拟」。同一页显示当前步骤、总体进度和分步页进度，结论用文字标成通过、失败、已取消或未测，并同时标出「模拟」。总耗时未知，所以不显示剩余时间。点「取消」后会给出取消反馈，尚未写入的页不会再写，状态保持「已取消」。失败和拒绝会给出原因和下一步，不展示堆栈。错误分布、UBER/RBER、忙时间和电压温度曲线标为未实现，不画伪造曲线。逐步结果、错误位置和配方快照默认收起，需要时再展开。CSV 的任务编号、结论和错误计数与页面一致。

专家配方：同一页下方编辑受限 JSON，点「校验并不运行」。合法配方再点上面的「运行模拟」。带 `script`、未知字段、未批准版本或越界地址的配方会被拒绝，不会执行其中的文本。

未知设备编号会改用模拟替身 `sim-dut-1`，页面和 CSV 都会写明替换原因。吞吐、电压、温度保持「未测量 / 未实现」。

历史列表来自 SQLite。重启服务后，已结束任务仍可查询；当时还在跑的任务标为「已中断」，不会接着写。CSV 列与页面上的任务字段、步骤和错误位置一致。

固定种子可以复现。`increment` 的第 `i` 个字节是 `(seed + ((block * 65536 + page) * pageSize + i)) mod 256`。`prng` 为按页重置的 xorshift32。`constant` 整页填同一个字节。

## 本版已落地的子集

| 子项 | 本版 |
| --- | --- |
| F01 基础闭环 | 模拟擦除、图案写入、读回比较、进度、取消、SQLite、CSV |
| F03 图案 | increment、constant、prng |
| F07 受限配方 | 同一页的简单操作，加白名单 JSON 编辑和校验 |
| F11 设备信息 | 合成标识、状态、参数页 |
| F12 基础 UI | 中文测试页、进度、取消、历史、CSV |
| F28 基础结果 | 配方快照、种子、逐步结果，CSV 与 SQLite 一致 |

以上都是子集，不是对应 F 编号的完整验收。

## 仍未实现、但保留的范围

一期 P0 后续增量：F02 命令抽象，F04 忙时间，F05 ECC/UBER，F06 规模和速率，F07 专业脚本与 SSL 兼容，F08 P/E 长测恢复，F09 写干扰，F10 读干扰，F12 完整双模式，F13 错误分布，F14 电压拉偏，F15 时序校准，F16 三温，F17 公开命令最小集，F19 Retry，F20 五级分档和 MES，F21 QLC，F25 Handler，F26 集群，F27 权限审计，F28 完整报告，F29 全自动产线。

现行 P1：F07 单步调试，F17 完整 Vth，F18 保持向导，F28 对比交互。

现行 P2：F22 认证，F23 AI，F24 FTL 教学模拟器。F24 与本设备模拟器不是同一功能。

完整对照也可以看运行中的 `GET /api/meta`。其中 `jsonEntryIsSslCompatible` 为 false。

## 明确不做

- 不把模拟计数写成实测 MT/s、电压精度、温度或量产结论。
- 不实现真实电压、温箱、Handler 或旧 SSL 导入。
- 不读取反编译、旧程序或专有 datasheet。
- 非法配方不会被当成脚本执行。
