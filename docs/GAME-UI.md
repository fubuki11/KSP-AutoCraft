# 0.6.1 游戏内操作与自动启动

`python -m ksp_autocraft ... health` 是连通性检查，不是启动服务的命令。本版把这项检查自动放到了 VAB/SPH 的启动流程，并提供完整游戏内设计面板。

## 升级一次

保存并退出 KSP 和 CKAN，在仓库根目录执行，按实际情况修改游戏目录：

```powershell
.\scripts\install-local.ps1 -KspRoot 'D:\steam\steamapps\common\Kerbal Space Program' -Upgrade
```

Python 客户端现随插件安装到 `GameData/KSPAutoCraft/Client/ksp_autocraft`，不需要每次手动设置 `PYTHONPATH`。

安装脚本会自动识别 Python 3.10+ 的实际可执行文件路径，并在首次配置时创建：

`GameData/KSPAutoCraft/PluginData/desktop.json`

已有桌面配置迁移到 v2，保留 Python 路径和生成权限，移除独立模型配置字段。手工安装时，可在游戏内“设置”中填写 Python 路径；模型统一在 AI Hub 面板配置。

## 进入 VAB/SPH 后

1. 主菜单启动时检查 AI Hub 是否安装/加载，进入编辑器再次检查，并自动启动本机 API。
2. 编辑器就绪后，后台启动一次隐藏的 Python 连通性检查。
3. 面板显示 **Python/API：已连接**，并检查 AI Hub 服务、当前模型和凭据状态。
4. **允许生成候选文件** 默认启用，并记住你的选择；下次进入会恢复此设置。

自动检查不会调用模型生成，也不会创建飞船。模型配置可读取不等于远端推理一定可用，网络与额度会在你点击设计时验证。

## 在游戏内设计

1. 点击合同选择按钮，选已接合同或待接合同；也可选自由设计。
2. 在文本框输入自然语言需求。
3. 可填写预算和最大湿质量；留空使用默认约束，职业模式仍受当前资金限制。
4. 点击 **设计并生成候选**。
5. Python 在后台读取合同/部件、调用模型、校验并修正。面板显示进行时间，可取消任务。
6. 成功后自动生成候选 `.craft`，在面板内查看任务步骤与假设。
7. 点击原有 **Review / load candidate → Back up current ship and load** 进行确认载入。

不会自动载入、发射或完成合同。仍需检查部件位置、分级、燃料流、电力及实际飞行条件。

## 模型登录

在右上角 AI Hub 面板选择供应商、保存 API Key 或完成该 Hub 配置支持的登录，并应用模型。AutoCraft 自动读取同一安装中的 Hub 连接文件，无需填写模型 JSON 路径。详见 [AIHUB.md](AIHUB.md)。

若 Hub 未安装、服务未启动或模型未配置，AutoCraft 会显示对应提示。模型问题在 Hub 修正后点击 AutoCraft 的“连接检查”。

## 退出、取消与文件

- 离开编辑器或取消任务时，只终止本插件启动的 Python 子进程。
- 退出旧场景后，不会把旧任务结果自动应用到新场景。
- 设计 JSON 和报告保存在 `PluginData/Designs`；候选 `.craft` 在 `PluginData/Builds`。
- 若生成权限在设计途中被关闭，方案文件仍会保留；重新开启后可用面板的“用上次方案重新生成候选”。
- 连接检查超时为 20 秒，设计后台进程硬上限为 35 分钟。Hub 单次请求默认 300 秒、最多 600 秒；全部生成/修正仍共享最多三次调用，其中模型输出恢复最多一次。可在 AI Hub 的生成设置中调整。

文本框获得焦点时会锁住编辑器快捷键，点击窗口外部可退出输入焦点，避免打字时误触游戏操作。

## 设置字段

`desktop.json` 只包含 `schemaVersion: 2`、`pythonExecutable` 和 `autoEnableGeneration`，不存储模型配置、密钥或登录令牌。进入编辑器时固定自动检查。
