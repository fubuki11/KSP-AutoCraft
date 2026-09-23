# 0.7.0：自然语言与合同驱动设计

日常操作使用 [游戏内面板](GAME-UI.md)。本页是命令行入口；所有模型请求统一通过已安装的 **AI Hub**，具体配置见 [AIHUB.md](AIHUB.md)。

## 流程与约束

**读取实际合同/星系/可用部件 → 通过 AI Hub 生成构型 → 游戏结构与性能检查 → 有限次修正 → 合同逐项评估 → 输出方案与报告。**

合同接口只读，不接受、取消、完成合同，不修改合同条件。报告状态包括 `failed`、`flight_required`、`needs_review`、`prerequisites_met`、`no_contract`；先决条件和性能筛选通过不等于实际飞行或合同完成。

火箭与飞机的筛选模型和近似边界见 [PERFORMANCE.md](PERFORMANCE.md)。构型连接、分级、操纵面、燃料、电力与实际飞行仍需在游戏中检查。

## 配置与检查

先安装 KSPAIHub 0.4.0+，在 Hub 中配置供应商/登录并应用模型；再进入 VAB/SPH。

在仓库根目录执行，按实际情况修改游戏目录：

```powershell
$env:PYTHONPATH = Join-Path $PWD 'client'
$ksp = 'D:\steam\steamapps\common\Kerbal Space Program'
New-Item -ItemType Directory -Path '.\plans' -Force | Out-Null
python -m ksp_autocraft --ksp-root $ksp hub-health
python -m ksp_autocraft --ksp-root $ksp health
python -m ksp_autocraft --ksp-root $ksp contracts --state active
python -m ksp_autocraft --ksp-root $ksp contracts --state offered
python -m ksp_autocraft --ksp-root $ksp world
```

`hub-health` 检查 Hub 安装、服务和模型选择；`health` 检查 AutoCraft 编辑器 API。两者都不会发起模型生成。

AutoCraft 自动使用同一游戏目录中的 Hub 连接文件和消费端 ID `KSPAutoCraft`，不再使用独立模型配置文件、模型环境变量或 `--llm-config`。

## 读取并使用合同

```powershell
$contract = '替换为合同列表中的 GUID'
python -m ksp_autocraft --ksp-root $ksp contract $contract
python -m ksp_autocraft --ksp-root $ksp design '为这个合同设计低成本火箭，考虑回收，用中文写任务步骤' --contract $contract --budget 30000 --output '.\plans\new-contract-rocket.json'
```

`active` 表示已接合同，`offered` 表示可接合同；沙盒/科研模式可能没有合同。可以为待接合同设计，但实际执行前应在游戏内接取。若接取后才临时解锁测试部件，应先接取再设计。

自由设计省略 `--contract`：

```powershell
python -m ksp_autocraft --ksp-root $ksp design '设计一架低成本单人喷气教练机' --vehicle aircraft --target-altitude 5000 --cruise-speed 150 --budget 20000 --output '.\plans\new-trainer.json'
```

飞机使用 SPH，火箭使用 VAB。`--max-mass` 限制湿质量，`--attempts` 限制模型生成/修正次数（1–3）；职业模式还受当前资金约束。模型调用可能消耗对应供应商额度。

输出 `.json` 构型及同名 `.report.json`，包含合同快照、检查结果、性能指标、步骤及假设，不覆盖已有文件。设计中切换存档、编辑器或合同状态变化时会停止。

## 审查与生成

```powershell
$plan = '.\plans\new-contract-rocket.json'
python -m ksp_autocraft --ksp-root $ksp validate $plan
python -m ksp_autocraft --ksp-root $ksp build $plan
```

`build` 需要游戏面板允许生成文件，随后通过 **Review / load candidate → Back up current ship and load** 确认载入。命令行设计本身不会自动提交 build、载入或发射。

## 常见问题

- Hub 缺失/版本过旧：通过 CKAN 安装或升级 Hub，然后重启 KSP。
- Hub 服务未启动：在 AI Hub 面板点击 **Start service**，然后重新连接检查。
- 占位模型、未登录或权限问题：在 Hub 配置并应用模型；检查对应账户状态。
- 已在 Hub 切换全局模型却未生效：检查 `KSPAutoCraft` 消费端是否有覆盖；已有设计任务会固定原模型，下次设计才读取新选择。
- `No usable unlocked parts`：检查存档、科技和实验部件状态。
- 结构/性能无法通过：查看报告反馈，调整预算、任务目标或部件条件。
- 输出截断：在 Hub 配置档调整输出 token 上限，或缩小任务规模。
