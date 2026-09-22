# KSP AutoCraft 0.6.1

**作者：fubuki11st** · **MIT License** · **KSP 1.12.5 / Windows x64**

在 KSP 的 VAB/SPH 中通过自然语言和合同要求生成候选构型。AutoCraft 读取实际游戏数据，经模型规划、结构/性能检查和有界修正后写出 `.craft`，由玩家确认载入。

**依赖：[KSP AI Hub 0.3.0+](https://github.com/fubuki11/KSP-AIHub)。** 供应商、API Key、OAuth 和模型选择统一由 Hub 管理；本项目不包含独立模型登录。

## 功能

- 读取当前存档的部件、科技/购买状态、资源、连接节点、发动机与气动信息，包括模组部件。
- 读取合同要求树和实际星系参数，保留 AND/OR/XOR 及需要飞行/人工审查的条件。
- 火箭与飞机分别选型；支持栈式/表面连接、SPH 水平姿态、翼面方向和镜像配对。
- 筛选分级 Δv、TWR、燃料可达性，以及飞机升力/阻力、进气、航时、稳定裕度和支撑布局。
- 游戏内选择合同、输入需求、查看报告、生成候选与手动确认载入。
- 模型侧目录压缩、明确的截断/格式错误恢复；全部调用共享最多三次尝试，模型错误恢复最多一次。
- 本机编辑器 HTTP API、Python 命令行工具和 CKAN 兼容打包。

性能检查是有近似边界的飞行前筛选，不是完整轨迹/CFD 仿真。通过检查不等于实际飞行或合同完成；仍需检查布局、分级、操控与任务执行。不会自动发射、接受或完成合同。

## 文档

| 文档 | 内容 |
| --- | --- |
| [游戏内操作](docs/GAME-UI.md) | 自动连接检查、设计面板、报告与候选载入 |
| [AI Hub 接入](docs/AIHUB.md) | 依赖检测、模型选择和有界恢复 |
| [自然语言/合同设计](docs/NATURAL-DESIGN.md) | 工作流程与 CLI 示例 |
| [性能筛选](docs/PERFORMANCE.md) | 火箭/飞机检查及近似边界 |
| [HTTP API](docs/API.md) | 编辑器接口、认证与能力 |
| [构型 Schema](docs/craft-plan.schema.json) | 模型/工具提交的构型格式 |
| [CKAN 安装问题](docs/CKAN-IMPORT-FIX.md) | 导入缓存、本地仓库与文件占用 |

## 从源码构建与安装

需要 Git、Python 3.10+、.NET SDK 和本机 KSP 1.12.5。DLL 目标为 .NET Framework 4.7.2；独立 C# 测试使用 .NET 10。游戏及 Unity 程序集只作本地编译引用，不随发布包分发。

先按 [AI Hub 仓库](https://github.com/fubuki11/KSP-AIHub) 的说明安装依赖，再构建本项目。以下 PowerShell 命令中的 `$ksp` 应改为实际游戏目录：

```powershell
$project = Join-Path $PWD 'KSP-AutoCraft'
git clone https://github.com/fubuki11/KSP-AutoCraft.git $project
$ksp = 'D:\steam\steamapps\common\Kerbal Space Program'
& (Join-Path $project 'scripts\build.ps1') -KspRoot $ksp
```

保存并退出 KSP、正常关闭 CKAN 后安装；已有旧版本时保留 `-Upgrade`：

```powershell
& (Join-Path $project 'scripts\install-local.ps1') -KspRoot $ksp -Upgrade
```

脚本注册/刷新 `KSPAutoCraft-local` 本地索引，通过 CKAN 安装明确版本，验证文件归属和内容，并初始化或迁移桌面启动设置。已安装内容被修改时会停止而不是直接覆盖。

**Import downloaded mods… 可能只缓存未收录 Mod，不代表实际安装。** 本地 ZIP 和仓库索引应保留在构建位置，供 CKAN 使用。安装后可在 CKAN 的 Installed / 已安装筛选中查找 `KSPAutoCraft`。

有 Release ZIP 时也可手工把 `GameData/KSPAutoCraft` 复制到游戏 `GameData`；仍需先安装 AI Hub，并在 AutoCraft 设置中填写实际 Python 可执行文件路径。

## 游戏内使用

1. 启动 KSP，在 **AI Hub** 面板配置供应商、认证和模型，点击 **Apply global model**。
2. 火箭进入 VAB，飞机进入 SPH。AutoCraft 自动检查 Hub、Python 和编辑器 API。
3. 选择合同或自由设计，输入需求；可设置预算、质量和性能目标。
4. 点击 **设计并生成候选**，查看任务报告与性能反馈。
5. 点击 **Review / load candidate → Back up current ship and load** 确认载入，再在编辑器中检查和保存。

Hub 缺失或未加载时会提示依赖问题；服务未启动、模型未配置、预算截断和连接故障会分别报告。可在 Hub 的 **Generation limits / reasoning** 调整预算与推理参数。

AutoCraft 默认继承 Hub 全局选择；若 `KSPAutoCraft` 消费端有独立覆盖，可在 Hub 点击 **Use global default** 清除。已有设计任务固定原模型，下次设计读取新选择。

## Python 工具

```powershell
$env:PYTHONPATH = Join-Path $project 'client'
python -m ksp_autocraft --ksp-root $ksp hub-health
python -m ksp_autocraft --ksp-root $ksp health
python -m ksp_autocraft --ksp-root $ksp catalog --search pod --limit 30
python -m ksp_autocraft --ksp-root $ksp contracts --state active
python -m ksp_autocraft --ksp-root $ksp world
python -m ksp_autocraft --ksp-root $ksp validate (Join-Path $project 'examples\starter-stack.json')
```

`hub-health` 只检查 Hub；编辑器查询需游戏停留在 VAB/SPH。示例构型只用于结构测试，不代表能完成任务。

完整设计命令见 [NATURAL-DESIGN.md](docs/NATURAL-DESIGN.md)。已移除旧的 `--llm-config` 和独立模型凭据入口。

## 运行文件与边界

运行文件位于游戏 `GameData/KSPAutoCraft/PluginData`：

- `settings.json`：本机端口（默认 18080）及随机访问令牌。
- `desktop.json`：Python 路径和候选生成权限。
- `Designs/`：设计 JSON 与报告。
- `Builds/`：候选 `.craft`；`Backups/`：确认载入前的飞船备份。

这些文件、个人方案和凭据不进入源码或发布包。HTTP 仅监听 `127.0.0.1`，游戏 API 在主线程执行；没有远程载入、删除或发射接口。

表面放置、机身阻力和部分稳定性估计仍有近似；复杂燃料管、多模式/自定义发动机、FAR、变体和缩放等需额外适配。详见性能说明，不应把模型建议当作实飞验证。

## 测试

在仓库根目录执行：

```powershell
$env:PYTHONPATH = Join-Path $PWD 'client'
python -m unittest discover -s tests/python -v
dotnet run --project tests/KSPAutoCraft.CoreTests --configuration Release
dotnet run --project tests/KSPAutoCraft.JsonTests --configuration Release -- examples/starter-stack.json
dotnet run --project tests/KSPAutoCraft.ApiJsonTests --configuration Release
dotnet run --project tests/KSPAutoCraft.ClientProcessTests --configuration Release
dotnet run --project tests/KSPAutoCraft.TransportTests --configuration Release
dotnet run --project tests/KSPAutoCraft.DispatchTests --configuration Release
```

打包测试在构建后验证发布文件。`scripts/test-native-*.ps1` 和 CKAN 导入诊断需要对应的本机游戏/CKAN 程序集，测试中不会分发它们。

## 公开发布

源码通过 Git 管理，安装包通过 [GitHub Releases](https://github.com/fubuki11/KSP-AutoCraft/releases) 分发。准备 Release 时用公开 HTTPS 地址重新构建：

```powershell
& (Join-Path $project 'scripts\build.ps1') -KspRoot $ksp -DownloadUrl 'https://github.com/fubuki11/KSP-AutoCraft/releases/download/v0.6.1/KSPAutoCraft-0.6.1.zip'
```

输出插件 ZIP、独立客户端 ZIP、`.ckan` 和本地索引 ZIP。省略 `-DownloadUrl` 时使用本机 `file://` 地址；该地址不能作为其他电脑的公共下载地址。源码推送不会自动创建 Release，也不等于 CKAN 公共索引收录。
