# OPC UA Client Load Test

Windows 端 OPC UA Client 并发采集测试程序，用于同时连接 15 个以上 OPC UA Server endpoint，统计每个 endpoint 的符号表总点数、实际收到数据的点数，以及每个信号相邻数据时间间隔，评估 OPC UA Server 到 Client 的接收效率。

## 功能

- 多 endpoint 并发连接和订阅采集。
- 每个 endpoint 支持匿名或用户名密码登录。
- 支持 `None`、`Sign`、`SignAndEncrypt` 安全模式。
- 支持两种点表来源：
  - `nodeIds`：手工指定要订阅的变量 NodeId。
  - `browseRoots`：从一个或多个根节点向下浏览变量节点。
- 输出三类报告：
  - HTML 汇总报告，适合现场打开查看。
  - JSON 完整报告，适合二次分析。
  - CSV endpoint 汇总和信号明细，适合 Excel 分析。

## Windows 运行

安装 .NET 8 SDK 后，在 PowerShell 中执行：

```powershell
cd "C:\path\to\OPC UA Client tool"
copy .\OpcUaClientLoadTest\appsettings.sample.json .\OpcUaClientLoadTest\appsettings.json
notepad .\OpcUaClientLoadTest\appsettings.json
dotnet run --project .\OpcUaClientLoadTest\OpcUaClientLoadTest.csproj -- .\OpcUaClientLoadTest\appsettings.json
```

发布为 Windows x64 单文件程序：

```powershell
dotnet publish .\OpcUaClientLoadTest\OpcUaClientLoadTest.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

发布后的程序在：

```text
OpcUaClientLoadTest\bin\Release\net8.0\win-x64\publish\opcua-client-loadtest.exe
```

运行发布包：

```powershell
.\opcua-client-loadtest.exe .\appsettings.json
```

## HD_OPC_UA 项目测试配置

本工具已按 `/Users/frank/Documents/HD_OPC UA` 的当前 OPC UA Server 形态准备了专用配置：

- namespace URI：`urn:hd-opcua-gateway`
- 单 endpoint 默认端口：`opc.tcp://<host>:4840`
- 分片 endpoint 默认从 `4840` 递增，例如 `4840`、`4841`、`4842`
- OPC UA 输出定位：500 ms sampled/latest-value
- NodeId 形态：`ns=2;s=<HD channel_id>`，例如 `ns=2;s=Mac04\[45:496]`
- 在线根目录：`ns=2;s=HD Online Sampled Values`

快速生成 16 个 endpoint 的配置：

```powershell
.\opcua-client-loadtest.exe --init-hd-opcua-config --host 192.168.3.166 --base-port 4840 --shards 16 --duration-seconds 1800 --out .\appsettings.hd-opcua.json
.\opcua-client-loadtest.exe .\appsettings.hd-opcua.json
```

如果只验证当前 3 分片基线：

```powershell
.\opcua-client-loadtest.exe --init-hd-opcua-config --host 192.168.3.166 --base-port 4840 --shards 3 --duration-seconds 1800 --out .\appsettings.hd-opcua-3shards.json
.\opcua-client-loadtest.exe .\appsettings.hd-opcua-3shards.json
```

也可以直接复制 `OpcUaClientLoadTest\appsettings.hd-opcua.sample.json`，把 `127.0.0.1` 改成现场网关 IP。

如果现场 Server 的 namespace index 不是 `2`，先临时把 `browseRoots` 改成 `ObjectsFolder` 跑一次浏览；确认 `HD Online Sampled Values` 的实际 NodeId 后，再改回精确根节点，避免把 Server 诊断节点计入 HD 符号表总点数。

对 HD_OPC_UA 的验收口径建议：

- `defaultPublishingIntervalMs` / `defaultSamplingIntervalMs` 使用 `500`，与网关 `sample_period_ms = 500` 对齐。
- 15 个以上 endpoint 场景用于验证客户端和网关分片能力，但不要把单 endpoint 的 30k-50k 全量高频能力作为目标。
- 重点看每个 endpoint 的符号表总点数、实际数据点数、活跃比例、样本/秒，以及信号间隔 P95/P99/max。
- 若实际数据点数明显低于符号表总点数，优先排查 browse/monitored item 创建失败、队列溢出、Server 端数据年龄和 HD 源覆盖率。

## 配置说明

`durationSeconds` 是采集时长。现场建议至少 300 秒；如果要观察分钟级抖动，可设置 1800 秒以上。

`acceptUntrustedCertificates` 为 `true` 时会自动接受 Server 证书，适合临时测试；正式验收建议改为 `false` 并将 Server 证书放入信任目录。

每个 endpoint 可配置：

- `name`：报告中显示的名称。
- `url`：OPC UA endpoint URL，例如 `opc.tcp://192.168.1.20:4840`。
- `securityPolicy`：`None`、`Basic256Sha256` 等。
- `securityMode`：`None`、`Sign`、`SignAndEncrypt`。
- `userName` / `password`：可选。
- `publishingIntervalMs`：订阅发布周期。
- `samplingIntervalMs`：采样周期。
- `queueSize`：单点队列长度。
- `browseRoots`：浏览点表的根节点。
- `nodeIds`：直接订阅的变量 NodeId。

15 个以上 endpoint 时，在 `endpoints` 数组中继续追加配置即可。

## 统计口径

- 符号表总点数：配置中的 `nodeIds` 加上从 `browseRoots` 浏览到的变量节点去重后的数量。
- 实际数据点数：采集期间至少收到 1 个样本的信号数量。
- 相邻数据时间间隔：优先使用 OPC UA `SourceTimestamp`，没有时使用 `ServerTimestamp`，再没有时使用 Client 当前 UTC 时间。
- 间隔统计：每个信号输出 `min / avg / p50 / p95 / p99 / max`，单位毫秒。
- 接收效率：报告中以活跃点比例、总样本数、样本/秒、各信号 P95/P99 间隔综合判断。

## 输出目录

默认输出到 `reports`：

```text
opcua-loadtest-YYYYMMDD-HHMMSS.html
opcua-loadtest-YYYYMMDD-HHMMSS.json
opcua-loadtest-endpoints-YYYYMMDD-HHMMSS.csv
opcua-loadtest-signals-YYYYMMDD-HHMMSS.csv
```
