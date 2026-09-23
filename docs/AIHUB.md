# AutoCraft 0.7.0：统一通过 AI Hub 接入与有界恢复

AutoCraft 现在依赖 **KSPAIHub 0.4.0+**。供应商地址、API Key、OAuth、模型目录和模型选择全部在 **AI Hub** 管理，AutoCraft 不再提供独立模型配置入口。

0.7.0 对额度、格式、明确重复截断各使用一次对应恢复策略，计入总共最多三次尝试。同类错误反复出现时停止。重复截断按 Hub 的配置档策略临时降低思考，额度不足可以同时使用受限恢复预算；不会覆盖正常请求设置。网络失败、未完成的流和拒绝不会自动重放。恢复前重新检查存档/合同；成功报告记录 `modelRecoveries`、`modelRequests` 和 `promptBytes`。

游戏内失败任务会在 `PluginData/Diagnostics/task-<id>.json` 保存安全错误元数据，面板显示文件路径。通过其中的 Hub 请求编号可查找 `KSPAIHub/PluginData/Private/Diagnostics/generation-<requestId>.json`。这些记录不保存提示词、模型正文、思考内容或密钥。

部件目录采用列式编码和模型侧精度压缩，已选部件集合及校验器的原始数值保持完整。可以在 Hub 的 **Generation limits / reasoning** 面板调整预算和推理参数。

## 启动检查

1. 到达游戏主菜单时，AutoCraft 检查同一游戏安装中的 AI Hub DLL 是否存在、是否已加载及版本是否满足要求，并写入 KSP 日志。
2. 进入 VAB/SPH 后再次确认依赖，自动启动隐藏 Python 检查：Hub 连接文件、服务 API 版本、当前选择和凭据状态。
3. 面板显示 Hub 的依赖状态及有效配置档/模型。检查只调用本机 GET，不触发模型生成或登录令牌刷新。

缺少 Hub 时显示安装提示并禁用自动设计按钮。Hub 已安装但未启动时，提示打开右上角 **AI Hub → Start service**。没有有效模型或仍是占位模型 ID 时，提示在 Hub 选择并应用模型。修正后点击 AutoCraft 的 **连接检查**。

## 使用方法

- 在 **AI Hub** 设置供应商、登录和模型，点击 **Apply global model**。
- AutoCraft 自动查找 `GameData/KSPAIHub/PluginData/connection.json`，使用固定消费端 ID `KSPAutoCraft`。
- 默认继承 Hub 全局模型。若 Hub 中已有 `KSPAutoCraft` 独立覆盖，则遵循该覆盖；点击 Hub 的 **Use global default** 可恢复继承。
- 每次设计开始固定 Hub 的有效配置档与模型，任务内的修正使用同一选择；下次设计读取新选择。
- AutoCraft 设置只保留 Python 可执行文件路径和生成权限。无需选择模型 JSON、复制 Hub token、填写供应商密钥或设置模型环境变量。

## 配置迁移

`PluginData/desktop.json` 升级到 schemaVersion 2，只包含 `schemaVersion`、`pythonExecutable`、`autoEnableGeneration`。

安装脚本移除旧的 `modelConfig` 与 `autoCheckOnEnter` 字段，保留 Python 路径和生成权限；旧设置作为不活动备份保存在 `PluginData/Backups/desktop-before-aihub-*.json`。手工升级时，插件也能读取 v1 并写成 v2。进入编辑器的检查现在固定自动执行。

旧 `llm.local.json`、`AUTOCRAFT_LLM_*` 环境变量和 gateway JSON 都不再作为 AutoCraft 输入。供应商直连和读取 OpenCode OAuth 的实现已从客户端移除；原有 OAuth 若需继续使用，应在 AI Hub 中配置。

## 命令行

在仓库根目录执行，修改游戏目录为实际安装位置：

```powershell
$env:PYTHONPATH = Join-Path $PWD 'client'
$ksp = 'D:\steam\steamapps\common\Kerbal Space Program'
New-Item -ItemType Directory -Path '.\plans' -Force | Out-Null
python -m ksp_autocraft --ksp-root $ksp hub-health
python -m ksp_autocraft --ksp-root $ksp design '设计一架单人喷气教练机' --vehicle aircraft --output '.\plans\new-aircraft.json'
```

`hub-health` 不依赖编辑器 API，可以在 Hub 服务启动时单独诊断。`design` 必须指定游戏安装路径，不再接受 `--llm-config`。

现有目录、合同、物理计算、手工构型校验与候选文件工具仍是编辑器工具；自动设计的模型请求始终经由 AI Hub。
